#include "reportlayout.h"

#include <jansson.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "buffer.h"
#include "datamodel.h"

static void set_error(char *error, size_t error_size, const char *message)
{
    if (error != NULL && error_size > 0) {
        snprintf(error, error_size, "%s", message);
    }
}

static void set_errorf(char *error, size_t error_size, const char *format, ...)
{
    va_list args;

    if (error == NULL || error_size == 0) {
        return;
    }
    va_start(args, format);
    vsnprintf(error, error_size, format, args);
    va_end(args);
}

/* Returns a string member's value, or NULL when absent or not a string. */
static const char *json_text(json_t *object, const char *key)
{
    json_t *value = json_object_get(object, key);

    return json_is_string(value) ? json_string_value(value) : NULL;
}

/* Duplicates a string member, or NULL when absent/empty. */
static char *dup_text(json_t *object, const char *key)
{
    const char *text = json_text(object, key);

    if (text == NULL || text[0] == '\0') {
        return NULL;
    }
    return strdup(text);
}

/* Strips Power BI's surrounding single quotes from a stored text literal. */
static char *unquote(const char *value)
{
    size_t length = strlen(value);

    if (length >= 2 && value[0] == '\'' && value[length - 1] == '\'') {
        char *copy = (char *)malloc(length - 1);

        if (copy == NULL) {
            return NULL;
        }
        memcpy(copy, value + 1, length - 2);
        copy[length - 2] = '\0';
        return copy;
    }
    return strdup(value);
}

/* Grows an array of records by one, returning the zeroed new slot or NULL. */
static void *push_row(void **array, size_t *count, size_t element_size)
{
    void *grown = realloc(*array, (*count + 1) * element_size);

    if (grown == NULL) {
        return NULL;
    }
    *array = grown;
    memset((char *)grown + (*count * element_size), 0, element_size);
    return (char *)grown + ((*count)++ * element_size);
}

/*
 * Reads a visual's authored title, which Power BI stores as a single-quoted literal nested under
 * the visual's container objects: vcObjects.title[].properties.text.expr.Literal.Value.
 *
 * The title matters more than its depth suggests: it is the report author's own words for the
 * question the visual answers, which is exactly the phrasing a person would use when asking it
 * again.
 */
static char *read_title(json_t *single_visual)
{
    json_t *vc_objects = json_object_get(single_visual, "vcObjects");
    json_t *titles;
    size_t index;
    json_t *entry;

    if (!json_is_object(vc_objects)) {
        return NULL;
    }
    titles = json_object_get(vc_objects, "title");
    if (!json_is_array(titles)) {
        return NULL;
    }

    json_array_foreach(titles, index, entry) {
        json_t *properties = json_object_get(entry, "properties");
        json_t *text = properties != NULL ? json_object_get(properties, "text") : NULL;
        json_t *expr = text != NULL ? json_object_get(text, "expr") : NULL;
        json_t *literal = expr != NULL ? json_object_get(expr, "Literal") : NULL;
        const char *value = literal != NULL ? json_text(literal, "Value") : NULL;

        if (value != NULL && value[0] != '\0') {
            return unquote(value);
        }
    }

    return NULL;
}

/*
 * Resolves a selected expression to the table and column/measure it names.
 *
 * An aggregation is unwrapped to the field it aggregates ("sum of Sales Amount by month" is still
 * a question about Sales Amount), and a hierarchy level resolves to its level, which is the field
 * the visual actually groups by ("Month", not the "Fiscal" hierarchy containing it).
 *
 * Returns 0 on success and fills the out-parameters (pointers into the JSON, valid while the
 * document is), or -1 when the expression names no field at all.
 */
static int resolve_field(
    json_t *expression, json_t *sources_by_alias,
    const char **table, const char **name, int *is_measure)
{
    json_t *node;
    const char *keys[] = { "Column", "Measure", "HierarchyLevel" };
    size_t i;

    /* An aggregation wraps the real field. */
    node = json_object_get(expression, "Aggregation");
    if (json_is_object(node)) {
        json_t *inner = json_object_get(node, "Expression");

        return json_is_object(inner)
            ? resolve_field(inner, sources_by_alias, table, name, is_measure)
            : -1;
    }

    for (i = 0; i < sizeof(keys) / sizeof(keys[0]); i++) {
        json_t *source_ref;
        json_t *reference;
        const char *alias;
        const char *entity;

        node = json_object_get(expression, keys[i]);
        if (!json_is_object(node)) {
            continue;
        }

        *is_measure = (strcmp(keys[i], "Measure") == 0);

        if (strcmp(keys[i], "HierarchyLevel") == 0) {
            /* A hierarchy level's own source sits one level deeper, under its Hierarchy. */
            json_t *hierarchy_expression = json_object_get(node, "Expression");
            json_t *hierarchy = hierarchy_expression != NULL
                ? json_object_get(hierarchy_expression, "Hierarchy") : NULL;

            *name = json_text(node, "Level");
            reference = hierarchy;
        } else {
            *name = json_text(node, "Property");
            reference = node;
        }

        if (*name == NULL || reference == NULL) {
            return -1;
        }

        source_ref = json_object_get(reference, "Expression");
        source_ref = source_ref != NULL ? json_object_get(source_ref, "SourceRef") : NULL;
        if (!json_is_object(source_ref)) {
            return -1;
        }

        /* A reference names either an alias bound by the query's FROM, or an entity directly. */
        alias = json_text(source_ref, "Source");
        entity = json_text(source_ref, "Entity");

        if (alias != NULL) {
            const char *bound = json_text(sources_by_alias, alias);

            if (bound == NULL) {
                return -1;
            }
            *table = bound;
            return 0;
        }
        if (entity != NULL) {
            *table = entity;
            return 0;
        }
        return -1;
    }

    return -1;
}

/*
 * Reads one visual container, appending it to the page when it carries a question.
 *
 * A visual that projects no field is decoration: a shape, a textbox, an image. It states no
 * business question, so it is deliberately not recorded rather than stored as an empty visual.
 */
static int read_visual(
    json_t *container, int ordinal, ReportPage *page, char *error, size_t error_size)
{
    const char *config_text = json_text(container, "config");
    json_error_t json_error;
    json_t *config;
    json_t *single_visual;
    json_t *projections;
    json_t *prototype_query;
    json_t *select;
    json_t *from;
    json_t *selections_by_name;
    json_t *sources_by_alias;
    json_t *role_value;
    const char *role_name;
    ReportVisual *visual;
    size_t index;
    json_t *entry;
    int status = -1;

    if (config_text == NULL) {
        return 0;
    }

    /* The container's config is JSON stored as a string inside the outer JSON. */
    config = json_loads(config_text, 0, &json_error);
    if (config == NULL) {
        set_errorf(error, error_size,
            "a visual's configuration is not valid JSON (line %d: %s)",
            json_error.line, json_error.text);
        return -1;
    }

    single_visual = json_object_get(config, "singleVisual");
    if (!json_is_object(single_visual)) {
        /* A grouped container holds no query of its own; its children appear as their own
         * entries in the page's container list. */
        json_decref(config);
        return 0;
    }

    projections = json_object_get(single_visual, "projections");
    prototype_query = json_object_get(single_visual, "prototypeQuery");

    if (!json_is_object(projections) || json_object_size(projections) == 0
        || !json_is_object(prototype_query)) {
        json_decref(config);
        return 0;
    }

    select = json_object_get(prototype_query, "Select");
    from = json_object_get(prototype_query, "From");
    if (!json_is_array(select)) {
        json_decref(config);
        return 0;
    }

    /* Index the query's selections by the name a projection's queryRef points at, and its FROM
     * bindings by alias. Together these turn a projection into a concrete table and column. */
    selections_by_name = json_object();
    sources_by_alias = json_object();
    if (selections_by_name == NULL || sources_by_alias == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        goto done;
    }

    json_array_foreach(select, index, entry) {
        const char *name = json_text(entry, "Name");

        if (name != NULL) {
            json_object_set(selections_by_name, name, entry);
        }
    }

    if (json_is_array(from)) {
        json_array_foreach(from, index, entry) {
            const char *alias = json_text(entry, "Name");
            const char *entity = json_text(entry, "Entity");

            if (alias != NULL && entity != NULL) {
                json_object_set_new(sources_by_alias, alias, json_string(entity));
            }
        }
    }

    visual = (ReportVisual *)push_row(
        (void **)&page->visuals, &page->visual_count, sizeof(*page->visuals));
    if (visual == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        goto done;
    }

    visual->ordinal = ordinal;
    visual->visual_type = dup_text(single_visual, "visualType");
    if (visual->visual_type == NULL) {
        visual->visual_type = strdup("unknown");
    }
    visual->title = read_title(single_visual);

    /* Each projection bucket is a ROLE, and its name is kept verbatim: Category/Y for a chart,
     * Rows/Values for a matrix, Size for a map. The role is the author's own statement of the
     * question's shape. */
    json_object_foreach(projections, role_name, role_value) {
        if (!json_is_array(role_value)) {
            continue;
        }

        json_array_foreach(role_value, index, entry) {
            const char *query_ref = json_text(entry, "queryRef");
            json_t *selection;
            const char *table = NULL;
            const char *name = NULL;
            int is_measure = 0;
            VisualField *field;

            if (query_ref == NULL) {
                continue;
            }

            selection = json_object_get(selections_by_name, query_ref);
            if (!json_is_object(selection)) {
                /* The projection names a field the query does not select, so it cannot be
                 * resolved to a table; skipping it beats recording a field with no identity. */
                continue;
            }

            if (resolve_field(selection, sources_by_alias, &table, &name, &is_measure) != 0
                || table == NULL || name == NULL) {
                continue;
            }

            field = (VisualField *)push_row(
                (void **)&visual->fields, &visual->field_count, sizeof(*visual->fields));
            if (field == NULL) {
                set_error(error, error_size, "out of memory reading a visual's fields");
                goto done;
            }

            field->role = strdup(role_name);
            field->query_ref = strdup(query_ref);
            field->table = strdup(table);
            field->column_or_measure = strdup(name);
            field->is_measure = is_measure;
        }
    }

    /* Having resolved nothing, the visual states no question after all. Drop it rather than
     * emit a visual with an empty field list. */
    if (visual->field_count == 0) {
        free(visual->visual_type);
        free(visual->title);
        page->visual_count--;
    }

    status = 0;

done:
    json_decref(selections_by_name);
    json_decref(sources_by_alias);
    json_decref(config);
    return status;
}

static int read_page(json_t *section, int ordinal, ReportLayout *layout,
    char *error, size_t error_size)
{
    ReportPage *page;
    json_t *containers;
    size_t index;
    json_t *container;
    int visual_ordinal = 0;

    page = (ReportPage *)push_row(
        (void **)&layout->pages, &layout->page_count, sizeof(*layout->pages));
    if (page == NULL) {
        set_error(error, error_size, "out of memory reading the report's pages");
        return -1;
    }

    page->ordinal = ordinal;
    page->name = dup_text(section, "name");
    page->display_name = dup_text(section, "displayName");
    if (page->display_name == NULL && page->name != NULL) {
        page->display_name = strdup(page->name);
    }

    containers = json_object_get(section, "visualContainers");
    if (!json_is_array(containers)) {
        return 0;
    }

    json_array_foreach(containers, index, container) {
        visual_ordinal++;
        if (read_visual(container, visual_ordinal, page, error, error_size) != 0) {
            return -1;
        }
    }

    return 0;
}

int report_layout_read(const char *pbix_path, ReportLayout *layout, char *error, size_t error_size)
{
    Buffer member;
    char *json_text_utf8 = NULL;
    json_error_t json_error;
    json_t *root = NULL;
    json_t *sections;
    size_t index;
    json_t *section;
    int ordinal = 0;
    int status = -1;

    memset(layout, 0, sizeof(*layout));
    buffer_init(&member);

    if (pbix_read_member(pbix_path, "Report/Layout", &member, error, error_size) != 0) {
        goto done;
    }

    /* The layout is UTF-16LE, with no byte order mark in the files seen so far. */
    json_text_utf8 = pbix_utf16le_to_utf8(member.data, member.size);
    if (json_text_utf8 == NULL) {
        set_error(error, error_size, "the report layout is not valid UTF-16 text");
        goto done;
    }

    root = json_loads(json_text_utf8, 0, &json_error);
    if (root == NULL) {
        set_errorf(error, error_size,
            "the report layout is not valid JSON (line %d: %s)",
            json_error.line, json_error.text);
        goto done;
    }

    sections = json_object_get(root, "sections");
    if (!json_is_array(sections)) {
        /* A file can hold a model with no report built on it yet. That is not an error; it just
         * means there are no questions to record. */
        status = 0;
        goto done;
    }

    json_array_foreach(sections, index, section) {
        ordinal++;
        if (read_page(section, ordinal, layout, error, error_size) != 0) {
            goto done;
        }
    }

    status = 0;

done:
    if (root != NULL) {
        json_decref(root);
    }
    free(json_text_utf8);
    buffer_free(&member);
    if (status != 0) {
        report_layout_free(layout);
    }
    return status;
}

void report_layout_free(ReportLayout *layout)
{
    size_t page_index;

    for (page_index = 0; page_index < layout->page_count; page_index++) {
        ReportPage *page = &layout->pages[page_index];
        size_t visual_index;

        for (visual_index = 0; visual_index < page->visual_count; visual_index++) {
            ReportVisual *visual = &page->visuals[visual_index];
            size_t field_index;

            for (field_index = 0; field_index < visual->field_count; field_index++) {
                free(visual->fields[field_index].role);
                free(visual->fields[field_index].query_ref);
                free(visual->fields[field_index].table);
                free(visual->fields[field_index].column_or_measure);
            }
            free(visual->fields);
            free(visual->visual_type);
            free(visual->title);
        }
        free(page->visuals);
        free(page->name);
        free(page->display_name);
    }
    free(layout->pages);
    memset(layout, 0, sizeof(*layout));
}
