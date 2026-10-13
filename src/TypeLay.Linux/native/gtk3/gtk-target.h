#pragma once
#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../fcitx5/typelay-fcitx-v1.h"

#define TL_MAX_FRAME (256 * 1024)
#define TL_MAX_TEXT 8192

typedef struct Target Target;
struct Target {
    GtkWidget *widget; /* weak; removed in the widget's destroy handler */
    GObject *buffer;
    GObject *tag_table;
    GtkIMContext *im_context; /* weak; each provider call temporarily retains it */
    GThread *thread;
    gulong im_handler;
    TlFcitxStateV1 im_state;
    char *id;
    guint64 storage_revision;
    guint64 content_revision;
    guint64 input_revision;
    guint64 focus_revision;
    gboolean ime_known;
    gboolean im_observed;
    gboolean im_probing;
    gboolean composing;
    gboolean quarantined;
    gboolean mutating;
    gboolean dead;
};

const char *tl_string(JsonObject *object, const char *member);
gint64 tl_integer(JsonObject *object, const char *member, gint64 fallback);
JsonObject *tl_object(JsonObject *object, const char *member);
JsonObject *tl_copy(JsonObject *object);
gint64 tl_now(void);
void tl_send_event(const char *event, JsonObject *data);
void tl_invalidate(Target *target, gboolean text_changed, gboolean focus_changed);
void tl_forget(Target *target);
Target *tl_focused(void);
Target *tl_lookup(const char *id);
void tl_discover(GtkWidget *widget, gboolean before_input);
JsonObject *tl_describe(Target *target);
JsonObject *tl_capture(Target *target, JsonObject *authority);
JsonObject *tl_write(Target *target, JsonObject *request, gboolean direct);
gboolean tl_return(Target *target, JsonObject *authority);
gboolean tl_metadata_matches(Target *target, JsonObject *authority);
gboolean tl_ime_clear(Target *target);
gboolean tl_widget_ime_clear(GtkWidget *widget);
gboolean tl_return_ready(Target *target, JsonObject *authority);
void tl_clear_permit(void);
/* Cached metadata-only readiness; does not query the IM provider. */
gboolean tl_acceptance_ready(Target *target);

JsonObject *tl_geometry(Target *target);
