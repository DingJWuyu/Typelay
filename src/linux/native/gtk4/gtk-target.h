#pragma once
#include <gtk/gtk.h>
#include <json-glib/json-glib.h>
#include "../fcitx5/typelay-fcitx-v1.h"

#define TL_MAX_FRAME (256 * 1024)
#define TL_MAX_TEXT 8192

typedef struct Target Target;
typedef struct OrderedTicket OrderedTicket;
struct Target {
    GtkWidget *widget; /* weak exact GtkEntry or GtkTextView */
    GtkWidget *client; /* weak GtkText delegate or the TextView itself: actual IM owner */
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
JsonObject *tl_write_ordered(Target *target, JsonObject *request, OrderedTicket *ticket);
gboolean tl_prefix_supported(Target *target);
gboolean tl_prefix_begin(Target *target, guint64 id, const GdkEvent *event);
gboolean tl_prefix_finish(Target *target, guint64 id, TlFcitxStateV1 *fresh);
guint64 tl_ordered_pending_id(Target *target);
gboolean tl_ordered_finish(Target *target, guint64 id, TlFcitxStateV1 *fresh);
gboolean tl_prefix_transition(Target *target, guint64 id);
gboolean tl_prefix_validate(Target *target, guint64 id, const TlFcitxStateV1 *before,
    const TlFcitxStateV1 *current);
gboolean tl_prefix_prepare(Target *target, guint64 id, const TlFcitxStateV1 *current);
void tl_prefix_cancel(Target *target, GtkIMContext *identity, guint64 id);
/* Private frozen-ticket checks. Proof loss is distinct from explicit revoke. */
gboolean tl_ordered_transition(Target *target);
gboolean tl_ordered_observation(Target *target, const TlFcitxStateV1 *current);
gboolean tl_ordered_check(OrderedTicket *ticket, Target *target, gboolean source, gboolean proof);
gboolean tl_ordered_prepare(OrderedTicket *ticket, Target *target, const TlFcitxStateV1 *state);
gboolean tl_ordered_claim(OrderedTicket *ticket, Target *target);
void tl_drop_publication(void);
gboolean tl_return(Target *target, JsonObject *authority);
gboolean tl_metadata_matches(Target *target, JsonObject *authority);
gboolean tl_ime_clear(Target *target);
gboolean tl_widget_ime_clear(GtkWidget *widget);
gboolean tl_return_ready(Target *target, JsonObject *authority);
void tl_clear_permit(void);
/* Cached metadata-only readiness; does not query the IM provider. */
gboolean tl_acceptance_ready(Target *target);

JsonObject *tl_geometry(Target *target);

/* Applied locally, never inferred from the peer's platform/hello capability. */
JsonObject *tl_session_bindings(Target *target);
gboolean tl_controller_attached(GtkWidget *widget, const char *key);
gboolean tl_configuration_current(const char *generation);
gboolean tl_session_matches(JsonObject *bindings);
guint tl_session_keyval(gboolean accept);
gboolean tl_view_context_matches(Target *target, JsonObject *view);
/* One physical candidate-key pair, including editor return and refused presses. */
gboolean tl_dispatch_key(GdkEvent *event);
