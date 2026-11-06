#ifndef TYPELAY_GTK_WRITE_SETTLEMENT_H
#define TYPELAY_GTK_WRITE_SETTLEMENT_H

/* Source-thread facts only. An old Ctrl ticket may outlive publication, and a
 * synchronous writer may re-enter revoke handlers before returning a receipt. */
typedef struct {
    char *plan, *target, *generation;
    guint64 connection;
    guint admitting, in_flight;
} TlWriteSettlement;
static GHashTable *write_settlements;
static guint write_settlement_source;

static const char *write_publication_id(JsonObject *value) {
    return tl_string(tl_object(tl_object(value, "permit"), "plan"), "id");
}
static void write_settlement_free(gpointer value) {
    TlWriteSettlement *record = value;
    g_free(record->plan); g_free(record->target); g_free(record->generation); g_free(record);
}
static gboolean write_settlements_flush(gpointer data) {
    (void)data;
    write_settlement_source = 0;
    if (!write_settlements) return G_SOURCE_REMOVE;
    GPtrArray *events = g_ptr_array_new();
    GHashTableIter iterator;
    gpointer value;
    const guint64 observed_connection = connection_generation;
    const gint64 now = tl_now();
    gint64 next = G_MAXINT64;
    g_hash_table_iter_init(&iterator, write_settlements);
    while (g_hash_table_iter_next(&iterator, NULL, &value)) {
        TlWriteSettlement *record = value;
        if (connection < 0 || record->connection != connection_generation) {
            g_hash_table_iter_remove(&iterator); continue;
        }
        if (record->admitting || record->in_flight) continue;
        gint64 until = g_strcmp0(write_publication_id(publication), record->plan) == 0
            ? tl_integer(tl_object(publication, "permit"), "expiresAtMs", -1) : -1;
        if (ordered && !ordered->revoked && !ordered->claimed
            && g_strcmp0(write_publication_id(ordered->publication), record->plan) == 0)
            until = MAX(until, ordered->accept_until);
        if (until >= now) { next = MIN(next, until - now + 1); continue; }
        JsonObject *event = json_object_new();
        json_object_set_string_member(event, "planId", record->plan);
        json_object_set_string_member(event, "target", record->target);
        json_object_set_string_member(event, "generation", record->generation);
        remember(record->plan);
        g_hash_table_iter_remove(&iterator);
        g_ptr_array_add(events, event);
    }
    if (next != G_MAXINT64)
        write_settlement_source = g_timeout_add((guint)MIN(next, (gint64)G_MAXUINT), write_settlements_flush, NULL);
    for (guint index = 0; index < events->len; index++) {
        JsonObject *event = g_ptr_array_index(events, index);
        if (connection >= 0 && observed_connection == connection_generation) tl_send_event("writeRetired", event);
        else json_object_unref(event);
    }
    g_ptr_array_unref(events);
    return G_SOURCE_REMOVE;
}
static void write_settlements_schedule(void) {
    if (!write_settlements || !g_hash_table_size(write_settlements)) return;
    if (write_settlement_source) g_source_remove(write_settlement_source);
    write_settlement_source = g_idle_add(write_settlements_flush, NULL);
}
static void write_settlement_admit(JsonObject *data) {
    const char *id = write_publication_id(data), *target = tl_string(data, "target"), *generation = tl_string(data, "generation");
    if (!id || !*id || strlen(id) > 256 || !target || !generation || already_consumed(id)) return;
    if (!write_settlements) write_settlements = g_hash_table_new_full(g_str_hash, g_str_equal, NULL, write_settlement_free);
    TlWriteSettlement *record = g_hash_table_lookup(write_settlements, id);
    if (!record) {
        record = g_new0(TlWriteSettlement, 1);
        record->plan = g_strdup(id); record->target = g_strdup(target); record->generation = g_strdup(generation);
        record->connection = connection_generation;
        g_hash_table_insert(write_settlements, record->plan, record);
    }
    record->admitting++;
}
static void write_settlement_admitted(JsonObject *data) {
    const char *id = write_publication_id(data);
    TlWriteSettlement *record = write_settlements && id ? g_hash_table_lookup(write_settlements, id) : NULL;
    if (record && record->admitting) record->admitting--;
    write_settlements_schedule();
}
static void write_settlement_begin(const char *id) {
    TlWriteSettlement *record = write_settlements && id ? g_hash_table_lookup(write_settlements, id) : NULL;
    if (record) record->in_flight++;
}
static void write_settlement_end(const char *id) {
    TlWriteSettlement *record = write_settlements && id ? g_hash_table_lookup(write_settlements, id) : NULL;
    if (record && record->in_flight) record->in_flight--;
    write_settlements_schedule();
}
static void write_settlement_completed(const char *id) {
    if (write_settlements && id) g_hash_table_remove(write_settlements, id);
}
static void write_settlements_reset(void) {
    if (write_settlement_source) g_source_remove(write_settlement_source);
    write_settlement_source = 0;
    if (write_settlements) g_hash_table_remove_all(write_settlements);
}

#endif
