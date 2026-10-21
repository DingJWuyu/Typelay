/* Private test control. Never compiled into the production module or launcher. */
static gint64 direct_sequence;
static guint direct_rejected;
static const char *direct_control_path;
static gboolean direct_control(gpointer data) {
    (void)data;
    if (report_closed) return G_SOURCE_REMOVE;
    if (!direct_control_path || !g_file_test(direct_control_path, G_FILE_TEST_EXISTS)) return G_SOURCE_CONTINUE;
    gchar *contents = NULL;
    gsize length = 0;
    if (!g_file_get_contents(direct_control_path, &contents, &length, NULL)) return G_SOURCE_CONTINUE;
    g_unlink(direct_control_path);
    JsonParser *parser = json_parser_new();
    gboolean parsed = length <= 512 && json_parser_load_from_data(parser, contents, (gssize)length, NULL);
    g_free(contents);
    JsonNode *root = parsed ? json_parser_get_root(parser) : NULL;
    JsonObject *command = root && JSON_NODE_HOLDS_OBJECT(root) ? json_node_get_object(root) : NULL;
    JsonNode *sequence = command ? json_object_get_member(command, "sequence") : NULL;
    JsonNode *action = command ? json_object_get_member(command, "action") : NULL;
    gboolean typed = command && json_object_get_size(command) == 2 && sequence && action
        && JSON_NODE_HOLDS_VALUE(sequence) && json_node_get_value_type(sequence) == G_TYPE_INT64
        && JSON_NODE_HOLDS_VALUE(action) && json_node_get_value_type(action) == G_TYPE_STRING;
    const char *name = typed ? json_node_get_string(action) : "";
    const char *allowed[] = {"restore", "epoch", "input", "context", "serial", "composition", "focus",
        "body", "selection", "rollback", "prepare-input"};
    gboolean known = FALSE;
    for (guint i = 0; i < G_N_ELEMENTS(allowed); i++) known |= g_strcmp0(name, allowed[i]) == 0;
    if (!typed || !known || json_node_get_int(sequence) != direct_sequence + 1 || direct_sequence >= 128) {
        direct_rejected++;
        g_object_unref(parser);
        return G_SOURCE_CONTINUE;
    }
    direct_sequence++;
    GtkTextBuffer *buffer = gtk_text_view_get_buffer(GTK_TEXT_VIEW(notification_widget));
    if (strcmp(name, "restore") == 0) {
        notification_generation++;
        notification_flags = TL_FCITX_KNOWN | TL_FCITX_FOCUSED;
        notification_prepare_input = FALSE;
        gtk_widget_grab_focus(notification_widget);
        gtk_text_buffer_set_text(buffer, "Controlled original source", -1);
        notification_emit(TL_FCITX_CHANGE_BINDING | TL_FCITX_CHANGE_INPUT);
    } else if (strcmp(name, "epoch") == 0) notification_emit(0);
    else if (strcmp(name, "input") == 0) notification_emit(TL_FCITX_CHANGE_INPUT);
    else if (strcmp(name, "context") == 0) {
        notification_generation++;
        notification_emit(TL_FCITX_CHANGE_BINDING);
    } else if (strcmp(name, "serial") == 0) {
        notification_serial++;
        notification_emit(0);
    } else if (strcmp(name, "composition") == 0) {
        notification_flags |= TL_FCITX_COMPOSING;
        notification_emit(TL_FCITX_CHANGE_INPUT);
    } else if (strcmp(name, "focus") == 0) {
        gtk_widget_grab_focus(report_first);
        notification_emit(TL_FCITX_CHANGE_FOCUS);
    } else if (strcmp(name, "body") == 0) gtk_text_buffer_set_text(buffer, "Changed by fixture before request", -1);
    else if (strcmp(name, "selection") == 0) {
        GtkTextIter first, last;
        gtk_text_buffer_get_bounds(buffer, &first, &last);
        gtk_text_buffer_select_range(buffer, &first, &last);
    } else if (strcmp(name, "rollback") == 0) notification_epoch--;
    else if (strcmp(name, "prepare-input") == 0) notification_prepare_input = TRUE;
    g_object_unref(parser);
    return G_SOURCE_CONTINUE;
}
static void direct_control_report(JsonBuilder *builder) {
    if (!direct_control_path) return;
    json_builder_set_member_name(builder, "directControl");
    json_builder_begin_object(builder);
    json_builder_set_member_name(builder, "sequence"); json_builder_add_int_value(builder, direct_sequence);
    json_builder_set_member_name(builder, "rejected"); json_builder_add_int_value(builder, direct_rejected);
    json_builder_end_object(builder);
}
static void direct_control_init(void) {
    if (g_strcmp0(notification_mode, "direct") != 0) return;
    direct_control_path = g_getenv("TYPELAY_FIXTURE_DIRECT_CONTROL");
    if (direct_control_path && g_path_is_absolute(direct_control_path)) g_timeout_add(10, direct_control, NULL);
    else direct_control_path = NULL;
}
