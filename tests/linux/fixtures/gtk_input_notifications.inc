/* Controlled IM port for the native notification contract only. Never enabled
 * in real-model desktop scopes, never evidence of real IME Clear or translation. */
typedef struct { GtkIMContext parent; } NotificationContext;
typedef struct { GtkIMContextClass parent; } NotificationContextClass;
G_DEFINE_TYPE(NotificationContext, notification_context, GTK_TYPE_IM_CONTEXT)
static guint notification_signal;
static guint64 notification_input = 47, notification_generation = 192, notification_epoch = 56;
static guint64 notification_serial = 1;
static guint notification_flags = TL_FCITX_KNOWN | TL_FCITX_FOCUSED;
static gboolean notification_prepare_input;
static GtkIMContext *notification_context_instance;
static GtkWidget *notification_widget;
static const char *notification_mode;
static gboolean notification_during_probe, notification_nested;
static void notification_emit(guint causes) {
    if (causes & TL_FCITX_CHANGE_INPUT) notification_input++;
    notification_epoch++;
    g_signal_emit(notification_context_instance, notification_signal, 0, causes);
}
static gboolean notification_inspect(GtkIMContext *context, GtkWidget *widget, TlFcitxStateV1 *state) {
    if (context != notification_context_instance || widget != notification_widget) return FALSE;
    if (notification_during_probe) {
        notification_during_probe = FALSE;
        notification_emit(g_strcmp0(notification_mode, "during-zero") == 0 ? 0 : TL_FCITX_CHANGE_INPUT);
    }
    *state = (TlFcitxStateV1){.size = sizeof *state, .version = TL_FCITX_ABI_V1,
        .context_generation = notification_generation, .epoch = notification_epoch,
        .input_epoch = notification_input, .server_serial = notification_serial, .applied_serial = notification_serial,
        .flags = notification_flags};
    return TRUE;
}
static gboolean notification_validate(GtkIMContext *context, GtkWidget *widget, const TlFcitxStateV1 *state) {
    return context == notification_context_instance && widget == notification_widget
        && state->context_generation == notification_generation && state->input_epoch == notification_input
        && state->epoch == notification_epoch && state->server_serial == notification_serial
        && state->applied_serial == notification_serial && state->flags == notification_flags
        && notification_flags == (TL_FCITX_KNOWN | TL_FCITX_FOCUSED) && gtk_widget_has_focus(widget);
}
static gboolean notification_prepare(GtkIMContext *context, GtkWidget *widget, const TlFcitxStateV1 *state) {
    if (g_strcmp0(notification_mode, "direct") != 0 || !notification_validate(context, widget, state))
        return FALSE; /* Notification-only cases never authorize a write. */
    if (notification_prepare_input) {
        notification_prepare_input = FALSE;
        notification_emit(TL_FCITX_CHANGE_INPUT);
    }
    return TRUE; /* Explicit controlled test port, not a real IME guarantee. */
}
static const TlFcitxCapabilityV1 notification_capability = {
    sizeof(TlFcitxCapabilityV1), TL_FCITX_ABI_V1,
    notification_inspect, notification_validate, notification_prepare
};
static void notification_context_class_init(NotificationContextClass *klass) {
    notification_signal = g_signal_new(TL_FCITX_CHANGED_V1, G_TYPE_FROM_CLASS(klass),
        G_SIGNAL_RUN_LAST, 0, NULL, NULL, NULL, G_TYPE_NONE, 1, G_TYPE_UINT);
}
static void notification_context_init(NotificationContext *context) {
    g_object_set_data(G_OBJECT(context), TL_FCITX_CAPABILITY_V1, (gpointer)&notification_capability);
}
static void notification_nested_callback(GtkIMContext *context, guint causes, gpointer data) {
    (void)context; (void)causes; (void)data;
    if (!notification_nested) return;
    notification_nested = FALSE;
    notification_emit(TL_FCITX_CHANGE_INPUT);
}
static gboolean notification_key(GtkWidget *widget, GdkEventKey *event, gpointer data) {
    (void)widget; (void)data;
    if (event->keyval != GDK_KEY_F6) return FALSE;
    if (g_strcmp0(notification_mode, "nested-complete") == 0) {
        notification_nested = TRUE;
        notification_emit(TL_FCITX_CHANGE_INPUT);
    } else if (g_strcmp0(notification_mode, "missing") == 0) notification_input += 15;
    else if (g_strcmp0(notification_mode, "rollback") == 0) notification_input--;
    else {
        for (guint n = 0; n < 15; n++) notification_emit(TL_FCITX_CHANGE_INPUT);
        if (g_strcmp0(notification_mode, "during") == 0 || g_strcmp0(notification_mode, "during-zero") == 0)
            notification_during_probe = TRUE;
        if (g_strcmp0(notification_mode, "context") == 0) {
            notification_generation++;
            notification_emit(TL_FCITX_CHANGE_BINDING);
        }
        if (g_strcmp0(notification_mode, "unknown") == 0) notification_emit(8);
    }
    return TRUE;
}
static void notification_fixture_init(GtkWidget *widget) {
    notification_mode = g_getenv("TYPELAY_FIXTURE_INPUT_NOTIFICATIONS");
    if (!notification_mode) return;
    notification_widget = widget;
    gtk_widget_grab_focus(widget);
    notification_context_instance = g_object_new(notification_context_get_type(), NULL);
    g_signal_connect(notification_context_instance, TL_FCITX_CHANGED_V1,
        G_CALLBACK(notification_nested_callback), NULL);
    g_object_set_data_full(G_OBJECT(widget), TL_FCITX_CONTEXT_V1,
        notification_context_instance, g_object_unref);
    g_signal_connect(widget, "key-press-event", G_CALLBACK(notification_key), NULL);
}
