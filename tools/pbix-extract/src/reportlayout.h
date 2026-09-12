/*
 * Reads a .pbix's VISUAL layer: the pages a person flips through, the visuals on each, and the
 * role every field plays in them.
 *
 * This is the other half of what a report knows, and the more direct half. The semantic model
 * (see metadata.h) says what CAN be asked; the visual layer is a record of what people actually
 * asked and considered worth putting on a page. A chart titled "Sales Amount by Category" is a
 * business question whose shape is already settled, and the role each field sits in -- the axis a
 * chart is broken down BY versus the value it plots -- is the part of that shape which no amount
 * of reading the model or the warehouse schema recovers.
 *
 * Unlike the model, this needs no decompression: the `Report/Layout` part is plain UTF-16LE JSON
 * inside the zip. Two of Power BI's habits still have to be undone. Several fields hold JSON as a
 * STRING inside the outer JSON (a visual's `config`), so they are parsed a second time; and text
 * literals are stored already single-quoted ('Revenue by Region').
 */

#ifndef PBIX_REPORTLAYOUT_H
#define PBIX_REPORTLAYOUT_H

#include <stddef.h>

/* One field or measure a visual projects, and the role it plays in the question. */
typedef struct {
    char *role;             /* Power BI's projection bucket: Category, Y, Rows, Values, Size, ... */
    char *query_ref;        /* the field's reference name within the visual's query */
    char *table;            /* the entity the field belongs to, as the visual's query named it */
    char *column_or_measure;
    int is_measure;         /* a model-computed measure rather than a stored column */
} VisualField;

/* One visual on a page. Only visuals that project at least one field are represented. */
typedef struct {
    int ordinal;            /* 1-based position within its page */
    char *visual_type;      /* areaChart, pivotTable, slicer, map, ... */
    char *title;            /* the authored title, or NULL when the visual has none */
    VisualField *fields;
    size_t field_count;
} ReportVisual;

/* One page of the report. */
typedef struct {
    int ordinal;            /* 1-based position within the report */
    char *name;             /* the page's internal identifier */
    char *display_name;     /* the page's title as a person sees it on the tab */
    ReportVisual *visuals;
    size_t visual_count;
} ReportPage;

typedef struct {
    ReportPage *pages;
    size_t page_count;
} ReportLayout;

/*
 * Reads the visual layer from a .pbix.
 *
 * Returns 0 on success, -1 on failure with a message in `error`. A report with no visuals is a
 * success with zero pages recorded, not a failure: a file can legitimately hold a model and no
 * built report yet.
 */
int report_layout_read(const char *pbix_path, ReportLayout *layout, char *error, size_t error_size);

/* Releases everything the layout owns. Safe on a zeroed layout. */
void report_layout_free(ReportLayout *layout);

#endif /* PBIX_REPORTLAYOUT_H */
