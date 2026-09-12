/*
 * pbix-extract: reads a Power BI report's semantic model and writes it as a SQLFlow YAML
 * specification.
 *
 * The purpose is to state what a report's model MEANS, in a form a SQL author (or a language
 * model asked to write SQL) can work from: the tables and columns that exist, what each measure
 * computes, which columns are themselves computed, how the tables relate, and where each table's
 * data comes from. None of that is in the report's visual layer, and none of it can be inferred
 * from the warehouse schema alone.
 *
 * Run offline against a .pbix already on disk; the output is reviewed like any other estate
 * source before it is used.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "buffer.h"
#include "datamodel.h"
#include "metadata.h"
#include "reportlayout.h"
#include "yamlout.h"

#define ERROR_SIZE 1024

static void print_usage(FILE *stream, const char *program)
{
    fprintf(stream,
        "Usage: %s <report.pbix> [--name NAME] [--out FILE]\n"
        "\n"
        "Reads a Power BI report's semantic model and writes a SQLFlow YAML specification:\n"
        "tables, columns, measures (with their DAX), calculated columns, relationships, and\n"
        "each table's Power Query source.\n"
        "\n"
        "  --name NAME  subscriber name for the YAML entry (default: the file's base name)\n"
        "  --out FILE   write to FILE instead of standard output\n"
        "  --help       show this message\n",
        program);
}

/* Returns the base name of a path, without its directory or .pbix extension. */
static char *report_name_from_path(const char *path)
{
    const char *slash = strrchr(path, '/');
    const char *base = slash != NULL ? slash + 1 : path;
    const char *dot = strrchr(base, '.');
    size_t length = dot != NULL ? (size_t)(dot - base) : strlen(base);
    char *name = (char *)malloc(length + 1);
    size_t i;

    if (name == NULL) {
        return NULL;
    }

    /* A subscriber name is an identity in the estate, so spaces become underscores to keep it
     * usable as a YAML key and as a lineage node name. */
    for (i = 0; i < length; i++) {
        name[i] = base[i] == ' ' ? '_' : base[i];
    }
    name[length] = '\0';
    return name;
}

/* Emits "key: value" at the given indent, choosing a block scalar for multi-line values. */
static int emit_field(Buffer *out, int indent, const char *key, const char *value)
{
    if (value == NULL) {
        return 0;
    }
    if (yaml_indent(out, indent) != 0
        || buffer_append_str(out, key) != 0
        || buffer_append_str(out, ": ") != 0
        || yaml_text(out, value, indent + 2) != 0) {
        return -1;
    }
    /* yaml_text ends a block scalar with its own newline; a quoted scalar needs one added. */
    if (out->size > 0 && out->data[out->size - 1] != '\n') {
        return buffer_append_str(out, "\n");
    }
    return 0;
}

/*
 * Emits the report's visual layer: the pages, and on each the visuals that carry a question.
 *
 * This section is the record of what people actually asked. Where the model says what could be
 * queried, a visual says what someone decided was worth putting on a page, in which shape: its
 * title is the question in the author's own words, and each field's role says whether it is the
 * axis the answer is broken down by or the value being measured.
 */
static int emit_report(Buffer *out, const ReportLayout *layout)
{
    size_t page_index;

    if (layout->page_count == 0) {
        return 0;
    }

    if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "report:\n") != 0) {
        return -1;
    }

    for (page_index = 0; page_index < layout->page_count; page_index++) {
        const ReportPage *page = &layout->pages[page_index];
        size_t visual_index;

        if (yaml_indent(out, 6) != 0
            || buffer_append_str(out, "- page: ") != 0
            || yaml_quoted(out, page->display_name != NULL ? page->display_name : "") != 0
            || buffer_append_str(out, "\n") != 0) {
            return -1;
        }

        if (page->visual_count == 0) {
            continue;
        }

        if (yaml_indent(out, 8) != 0 || buffer_append_str(out, "visuals:\n") != 0) {
            return -1;
        }

        for (visual_index = 0; visual_index < page->visual_count; visual_index++) {
            const ReportVisual *visual = &page->visuals[visual_index];
            size_t field_index;

            if (yaml_indent(out, 10) != 0
                || buffer_append_str(out, "- visualType: ") != 0
                || yaml_quoted(out, visual->visual_type != NULL ? visual->visual_type : "") != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }

            if (visual->title != NULL) {
                if (yaml_indent(out, 12) != 0
                    || buffer_append_str(out, "title: ") != 0
                    || yaml_quoted(out, visual->title) != 0
                    || buffer_append_str(out, "\n") != 0) {
                    return -1;
                }
            }

            if (visual->field_count == 0) {
                continue;
            }

            if (yaml_indent(out, 12) != 0 || buffer_append_str(out, "fields:\n") != 0) {
                return -1;
            }

            for (field_index = 0; field_index < visual->field_count; field_index++) {
                const VisualField *field = &visual->fields[field_index];

                if (yaml_indent(out, 14) != 0
                    || buffer_append_str(out, "- role: ") != 0
                    || yaml_quoted(out, field->role != NULL ? field->role : "") != 0
                    || buffer_append_str(out, "\n") != 0) {
                    return -1;
                }
                if (yaml_indent(out, 16) != 0
                    || buffer_append_str(out, "table: ") != 0
                    || yaml_quoted(out, field->table != NULL ? field->table : "") != 0
                    || buffer_append_str(out, "\n") != 0) {
                    return -1;
                }
                if (yaml_indent(out, 16) != 0
                    || buffer_append_str(out, "field: ") != 0
                    || yaml_quoted(out,
                           field->column_or_measure != NULL ? field->column_or_measure : "") != 0
                    || buffer_append_str(out, "\n") != 0) {
                    return -1;
                }
                if (field->is_measure
                    && (yaml_indent(out, 16) != 0
                        || buffer_append_str(out, "isMeasure: true\n") != 0)) {
                    return -1;
                }
            }
        }
    }

    return 0;
}

static int emit_spec(
    Buffer *out, const ModelSpec *spec, const ReportLayout *layout,
    const char *name, const char *source_file)
{
    size_t i;

    if (buffer_append_str(out,
            "# Generated by tools/pbix-extract from a Power BI report.\n"
            "# Two halves: the semantic model (what its tables, columns, measures and\n"
            "# relationships are) and the report itself (the pages and visuals built on that\n"
            "# model, and the role each field plays in them). Review before use.\n"
            "#\n") != 0) {
        return -1;
    }
    if (buffer_append_str(out, "# Source report: ") != 0
        || buffer_append_str(out, source_file) != 0
        || buffer_append_str(out, "\n\n") != 0) {
        return -1;
    }

    if (buffer_append_str(out, "subscribers:\n") != 0
        || yaml_indent(out, 2) != 0
        || buffer_append_str(out, name) != 0
        || buffer_append_str(out, ":\n") != 0
        || yaml_indent(out, 4) != 0
        || buffer_append_str(out, "type: PowerBI\n") != 0) {
        return -1;
    }

    /* Tables and their columns, with the model's declared type for each. This is the vocabulary
     * every other section refers to. */
    if (spec->column_count > 0) {
        const char *current_table = NULL;

        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "tables:\n") != 0) {
            return -1;
        }
        for (i = 0; i < spec->column_count; i++) {
            const Column *column = &spec->columns[i];

            if (column->table == NULL || column->column == NULL) {
                continue;
            }

            if (current_table == NULL || strcmp(current_table, column->table) != 0) {
                current_table = column->table;
                if (yaml_indent(out, 6) != 0
                    || buffer_append_str(out, "- name: ") != 0
                    || yaml_quoted(out, current_table) != 0
                    || buffer_append_str(out, "\n") != 0
                    || yaml_indent(out, 8) != 0
                    || buffer_append_str(out, "columns:\n") != 0) {
                    return -1;
                }
            }

            if (yaml_indent(out, 10) != 0
                || buffer_append_str(out, "- name: ") != 0
                || yaml_quoted(out, column->column) != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }
            if (column->data_type != NULL
                && emit_field(out, 12, "dataType", column->data_type) != 0) {
                return -1;
            }
        }
    }

    if (spec->measure_count > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "measures:\n") != 0) {
            return -1;
        }
        for (i = 0; i < spec->measure_count; i++) {
            const Measure *measure = &spec->measures[i];

            if (measure->name == NULL) {
                continue;
            }
            if (yaml_indent(out, 6) != 0
                || buffer_append_str(out, "- name: ") != 0
                || yaml_quoted(out, measure->name) != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }
            if (emit_field(out, 8, "table", measure->table) != 0
                || emit_field(out, 8, "dax", measure->expression) != 0
                || emit_field(out, 8, "description", measure->description) != 0) {
                return -1;
            }
        }
    }

    if (spec->calculated_column_count > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "calculatedColumns:\n") != 0) {
            return -1;
        }
        for (i = 0; i < spec->calculated_column_count; i++) {
            const CalculatedColumn *column = &spec->calculated_columns[i];

            if (column->name == NULL) {
                continue;
            }
            if (yaml_indent(out, 6) != 0
                || buffer_append_str(out, "- name: ") != 0
                || yaml_quoted(out, column->name) != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }
            if (emit_field(out, 8, "table", column->table) != 0
                || emit_field(out, 8, "dax", column->expression) != 0) {
                return -1;
            }
        }
    }

    if (spec->relationship_count > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "relationships:\n") != 0) {
            return -1;
        }
        for (i = 0; i < spec->relationship_count; i++) {
            const Relationship *relationship = &spec->relationships[i];

            if (relationship->from_table == NULL || relationship->to_table == NULL) {
                continue;
            }
            if (yaml_indent(out, 6) != 0
                || buffer_append_str(out, "- fromTable: ") != 0
                || yaml_quoted(out, relationship->from_table) != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }
            if (emit_field(out, 8, "fromColumn", relationship->from_column) != 0
                || emit_field(out, 8, "toTable", relationship->to_table) != 0
                || emit_field(out, 8, "toColumn", relationship->to_column) != 0
                || emit_field(out, 8, "cardinality", relationship->cardinality) != 0) {
                return -1;
            }
            /* An inactive relationship exists but is not applied unless a measure invokes it
             * (USERELATIONSHIP), so stating it is what keeps a generated join honest. */
            if (yaml_indent(out, 8) != 0
                || buffer_append_str(out, "active: ") != 0
                || buffer_append_str(out, relationship->active ? "true\n" : "false\n") != 0) {
                return -1;
            }
        }
    }

    if (spec->source_count > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "tableSources:\n") != 0) {
            return -1;
        }
        for (i = 0; i < spec->source_count; i++) {
            const TableSource *source = &spec->sources[i];

            if (source->table == NULL) {
                continue;
            }
            if (yaml_indent(out, 6) != 0
                || buffer_append_str(out, "- table: ") != 0
                || yaml_quoted(out, source->table) != 0
                || buffer_append_str(out, "\n") != 0) {
                return -1;
            }
            if (emit_field(out, 8, "powerQuery", source->expression) != 0) {
                return -1;
            }
        }
    }

    return emit_report(out, layout);
}

int main(int argc, char **argv)
{
    const char *pbix_path = NULL;
    const char *out_path = NULL;
    const char *explicit_name = NULL;
    char *report_name = NULL;
    char error[ERROR_SIZE];
    DataModel model;
    ModelSpec spec;
    ReportLayout layout;
    Buffer out;
    int status = EXIT_FAILURE;
    int i;

    memset(&model, 0, sizeof(model));
    memset(&spec, 0, sizeof(spec));
    memset(&layout, 0, sizeof(layout));
    buffer_init(&out);
    error[0] = '\0';

    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            print_usage(stdout, argv[0]);
            return EXIT_SUCCESS;
        }
        if (strcmp(argv[i], "--out") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "error: --out needs a file path\n");
                return EXIT_FAILURE;
            }
            out_path = argv[i];
        } else if (strcmp(argv[i], "--name") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "error: --name needs a value\n");
                return EXIT_FAILURE;
            }
            explicit_name = argv[i];
        } else if (argv[i][0] == '-') {
            fprintf(stderr, "error: unknown option '%s'\n", argv[i]);
            print_usage(stderr, argv[0]);
            return EXIT_FAILURE;
        } else if (pbix_path == NULL) {
            pbix_path = argv[i];
        } else {
            fprintf(stderr, "error: only one report can be read at a time\n");
            return EXIT_FAILURE;
        }
    }

    if (pbix_path == NULL) {
        print_usage(stderr, argv[0]);
        return EXIT_FAILURE;
    }

    if (data_model_open(pbix_path, &model, error, sizeof(error)) != 0) {
        fprintf(stderr, "error: %s\n", error);
        goto done;
    }

    if (model_spec_read(&model, &spec, error, sizeof(error)) != 0) {
        fprintf(stderr, "error: %s\n", error);
        goto done;
    }

    /*
     * The visual layer is read from a different part of the file and is independent of the model,
     * so a report whose layout cannot be read still yields a complete model specification. The
     * failure is reported and the section omitted, rather than losing the half that did work.
     */
    if (report_layout_read(pbix_path, &layout, error, sizeof(error)) != 0) {
        fprintf(stderr, "warning: the report's visuals could not be read: %s\n", error);
        memset(&layout, 0, sizeof(layout));
    }

    if (explicit_name != NULL) {
        report_name = strdup(explicit_name);
    } else {
        report_name = report_name_from_path(pbix_path);
    }
    if (report_name == NULL) {
        fprintf(stderr, "error: out of memory\n");
        goto done;
    }

    if (emit_spec(&out, &spec, &layout, report_name, pbix_path) != 0) {
        fprintf(stderr, "error: out of memory writing the specification\n");
        goto done;
    }

    if (out_path != NULL) {
        FILE *file = fopen(out_path, "wb");

        if (file == NULL) {
            fprintf(stderr, "error: '%s' could not be opened for writing\n", out_path);
            goto done;
        }
        if (fwrite(out.data, 1, out.size, file) != out.size) {
            fprintf(stderr, "error: '%s' could not be written\n", out_path);
            fclose(file);
            goto done;
        }
        if (fclose(file) != 0) {
            fprintf(stderr, "error: '%s' could not be closed cleanly\n", out_path);
            goto done;
        }
        {
            size_t visual_total = 0;
            size_t page_index;

            for (page_index = 0; page_index < layout.page_count; page_index++) {
                visual_total += layout.pages[page_index].visual_count;
            }

            fprintf(stderr,
                "wrote %s: %zu tables' columns, %zu measures, %zu calculated columns, "
                "%zu relationships, %zu table sources, %zu pages, %zu visuals\n",
                out_path, spec.column_count, spec.measure_count, spec.calculated_column_count,
                spec.relationship_count, spec.source_count, layout.page_count, visual_total);
        }
    } else {
        if (fwrite(out.data, 1, out.size, stdout) != out.size) {
            fprintf(stderr, "error: the specification could not be written to standard output\n");
            goto done;
        }
    }

    status = EXIT_SUCCESS;

done:
    free(report_name);
    buffer_free(&out);
    report_layout_free(&layout);
    model_spec_free(&spec);
    data_model_free(&model);
    return status;
}
