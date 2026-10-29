#ifndef TYPELAY_INDEX_ADAPTER_H
#define TYPELAY_INDEX_ADAPTER_H
#include <stdint.h>
#if defined(_WIN32)
#define INDEX_API __declspec(dllexport)
#else
#define INDEX_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Independent Index ABI1. Never exchange this handle with Hy ABI5. */
typedef struct index_handle index_handle;
typedef int32_t (*index_cancel_fn)(void *user_data);
typedef struct index_metrics {
    uint32_t struct_size, abi_version;
    uint32_t source_tokens, prompt_tokens, output_tokens, split_count;
    uint32_t roundtrip_ok, forbidden_source_tokens, reserved_spelling_count, user_defined_vocab_count;
    uint32_t decode_calls, decode_callbacks;
    int32_t eog_token, last_decode_code;
} index_metrics;
typedef struct index_term {
    const uint8_t *source_term;
    uint32_t source_length;
    const uint8_t *target_term;
    uint32_t target_length;
} index_term;
enum index_status {
    INDEX_OK=0, INDEX_INVALID=1, INDEX_IDENTITY=2, INDEX_LITERAL_UNSAFE=3,
    INDEX_CONTEXT_LIMIT=4, INDEX_OUTPUT_LIMIT=5, INDEX_CANCELLED=6,
    INDEX_DECODE_ERROR=7, INDEX_RESOURCE=8, INDEX_INTERNAL=9
};
enum index_direction {
    INDEX_ZH_JA=1, INDEX_JA_ZH=2, INDEX_ZH_KO=3, INDEX_KO_ZH=4,
    INDEX_ZH_FR=5, INDEX_FR_ZH=6, INDEX_ZH_ES=7, INDEX_ES_ZH=8,
    INDEX_ZH_DE=9, INDEX_DE_ZH=10, INDEX_ZH_PT=11, INDEX_PT_ZH=12,
    INDEX_ZH_EN=13, INDEX_EN_ZH=14
};
enum index_style { INDEX_PLAIN=0, INDEX_NATURAL=1, INDEX_FORMAL=2 };
enum index_layout { INDEX_LITERALS=1, INDEX_LINEBREAKS=2, INDEX_EDGE_WHITESPACE=4 };
INDEX_API int32_t index_open_v1(const char *cpu_backend_path, const char *model_path,
    index_handle **out, char *error, uint32_t error_capacity);
/* Single owner, whole-source only, UTF-8 bytes roundtrip without trimming or
   normalization. style=0/terms=0/layout=0 uses the plain official prompt;
   genuine requirements use canonical instTrans structure, with original bytes.
   1..64 optional pairs, terms 1..128 bytes each, <=8192 bytes total, no controls
   or surrounding whitespace. Structured literals never become prompt controls.
   Fixed greedy, context4096/new1024, 2 threads, no retry or truncated success.
   Source/output <=8192 bytes. Only actual EOS248044/EOT248046 complete success.
   Every failure publishes output_length=0; callbacks must not throw. */
INDEX_API int32_t index_translate_v1(index_handle *handle, uint32_t direction,
    uint32_t style, uint32_t layout, const index_term *terms, uint32_t term_count,
    const uint8_t *source, uint32_t source_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    index_metrics *metrics, index_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
/* Optional ABI1 extension. Both source and previous are separately checked
   literal UTF-8 data, never trusted prompt fragments. One greedy inference,
   original source meaning remains authoritative; no style/glossary combination. */
INDEX_API int32_t index_translate_alternative_v1(index_handle *handle, uint32_t direction,
    const uint8_t *source, uint32_t source_length,
    const uint8_t *previous, uint32_t previous_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    index_metrics *metrics, index_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
INDEX_API void index_close(index_handle *handle);
#ifdef __cplusplus
}
#endif
#endif
