/* Test-only shaping with the live widget's public Pango context. GtkTextView
 * has no public internal glyph-run accessor, so this is not a pixel capture.
 * Only the short appearance samples use it; normal input/motion probes do not. */
static void report_font_runs(JsonBuilder *builder, GtkWidget *widget, const char *text, const char *member) {
    const char *stage = g_getenv("TYPELAY_FIXTURE_REENTRY_STAGE");
    if (g_strcmp0(stage, "preview-colors") != 0 || strlen(text) > 256) return;
    json_builder_set_member_name(builder, member);
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "pangoVersion");
    json_builder_add_string_value(builder, pango_version_string());
    json_builder_set_member_name(builder, "observation");
    json_builder_add_string_value(builder, "live-widget-context-shaping");
    json_builder_set_member_name(builder, "runs");
    json_builder_begin_array(builder);
#if PANGO_VERSION_CHECK(1, 50, 0)
    PangoLayout *layout = gtk_widget_create_pango_layout(widget, text);
    PangoLayoutIter *iterator = pango_layout_get_iter(layout);
    guint count = 0;
    do {
        PangoLayoutRun *run = pango_layout_iter_get_run_readonly(iterator);
        if (!run || !run->item->analysis.font) continue;
        PangoFont *font = run->item->analysis.font;
        JsonNode *identity = g_object_get_data(G_OBJECT(font), "fixture-font-identity");
        if (!identity) {
            GBytes *serialized = pango_font_serialize(font);
            gsize length = 0;
            const char *data = g_bytes_get_data(serialized, &length);
            JsonParser *parser = json_parser_new();
            if (json_parser_load_from_data(parser, data, (gssize)length, NULL)) {
                identity = json_node_copy(json_parser_get_root(parser));
                g_object_set_data_full(G_OBJECT(font), "fixture-font-identity", identity,
                    (GDestroyNotify)json_node_unref);
            }
            g_object_unref(parser);
            g_bytes_unref(serialized);
        }
        guint missing = 0;
        for (int index = 0; index < run->glyphs->num_glyphs; index++)
            if (run->glyphs->glyphs[index].glyph & PANGO_GLYPH_UNKNOWN_FLAG) missing++;
        json_builder_begin_object(builder);
        json_builder_set_member_name(builder, "offset"); json_builder_add_int_value(builder, run->item->offset);
        json_builder_set_member_name(builder, "length"); json_builder_add_int_value(builder, run->item->length);
        json_builder_set_member_name(builder, "missingGlyphs"); json_builder_add_int_value(builder, missing);
        json_builder_set_member_name(builder, "font");
        if (identity) json_builder_add_value(builder, json_node_copy(identity));
        else json_builder_add_null_value(builder);
        json_builder_end_object(builder);
        count++;
    } while (count < 16 && pango_layout_iter_next_run(iterator));
    pango_layout_iter_free(iterator);
    g_object_unref(layout);
#else
    (void)widget;
#endif
    json_builder_end_array(builder);
    json_builder_end_object(builder);
}
