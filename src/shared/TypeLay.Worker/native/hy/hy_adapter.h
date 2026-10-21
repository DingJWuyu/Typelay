#ifndef TYPELAY_HY_ADAPTER_H
#define TYPELAY_HY_ADAPTER_H
#include <stdint.h>
#if defined(_WIN32)
#define HY_API __declspec(dllexport)
#else
#define HY_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Fixed ABI 5: no upstream llama structs cross the boundary. Single-threaded owner. */
typedef struct hy_handle hy_handle;
typedef int32_t (*hy_cancel_fn)(void *user_data);
typedef struct hy_metrics {
    uint32_t struct_size, abi_version;
    uint32_t source_tokens, prompt_tokens, output_tokens, split_count;
    uint32_t roundtrip_ok, forbidden_source_tokens, reserved_spelling_count, user_defined_vocab_count;
    uint32_t decode_calls, decode_callbacks;
    int32_t eog_token, last_decode_code;
} hy_metrics;
/* Natural C layout, no packing. All byte buffers remain caller-owned. */
typedef struct hy_term {
    const uint8_t *source_term;
    uint32_t source_length;
    const uint8_t *target_term;
    uint32_t target_length;
} hy_term;
enum hy_status {
    HY_OK=0, HY_INVALID=1, HY_IDENTITY=2, HY_LITERAL_UNSAFE=3,
    HY_CONTEXT_LIMIT=4, HY_OUTPUT_LIMIT=5, HY_CANCELLED=6,
    HY_DECODE_ERROR=7, HY_RESOURCE=8, HY_INTERNAL=9
};
enum hy_direction {
    HY_ZH_JA=1, HY_JA_ZH=2, HY_ZH_KO=3, HY_KO_ZH=4,
    HY_ZH_FR=5, HY_FR_ZH=6, HY_ZH_ES=7, HY_ES_ZH=8,
    HY_ZH_DE=9, HY_DE_ZH=10, HY_ZH_PT=11, HY_PT_ZH=12
};
enum hy_style { HY_NATURAL=1, HY_FORMAL=2 };
HY_API int32_t hy_open_v5(const char *cpu_backend_path, const char *model_path,
    hy_handle **out, char *error, uint32_t error_capacity);
/* direction: exact hy_direction value. Fixed greedy, 192 new tokens, context 1024.
   ABI5 explicitly derives model canonical text by CRLF->LF only. Original
   source bytes stay caller-owned and are never modified. Bare CR is rejected.
   All other bytes must roundtrip exactly; roundtrip_ok refers to canonical text.
   Outer product formatting/writeback must keep the original source/layout.
   On every failure *output_length=0; no partial output is published.
   metrics must have struct_size=sizeof(hy_metrics), abi_version=5.
   cancel callback runs from CPU decode and may be NULL; callback must not throw.
   The owner must not call close or another operation concurrently. */
HY_API int32_t hy_translate(hy_handle *handle, uint32_t direction,
    const uint8_t *source, uint32_t source_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    hy_metrics *metrics, hy_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
/* Explicit styles never fall back to the ordinary instruction. Unknown enum
   values fail before touching model memory; the same limits/cancellation apply. */
HY_API int32_t hy_translate_styled_v5(hy_handle *handle, uint32_t direction, uint32_t style,
    const uint8_t *source, uint32_t source_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    hy_metrics *metrics, hy_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
/* 1..64 pairs, each term 1..128 strict UTF-8 bytes, total <=8192 bytes.
   Terms cannot contain Unicode Cc controls or leading/trailing whitespace.
   Each term and source passes the same separate literal encoder and roundtrip
   gate. source_tokens/split_count/forbidden_source_tokens include all segments;
   roundtrip_ok is true only when every segment passed. Vocabulary inventory
   counts describe the single model, not repeated segment observations.
   The fixed reference-translation template is a soft model constraint only.
   Styles cannot be combined here. All other translate limits apply unchanged. */
HY_API int32_t hy_translate_glossary_v5(hy_handle *handle, uint32_t direction,
    const hy_term *terms, uint32_t term_count,
    const uint8_t *source, uint32_t source_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    hy_metrics *metrics, hy_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
/* Optional ABI5 extension. Requires explicit combined capability; unchanged layout
   and budgets. Terms and style apply to the same source in one decode. */
HY_API int32_t hy_translate_styled_glossary_v5(hy_handle *handle, uint32_t direction, uint32_t style,
    const hy_term *terms, uint32_t term_count,
    const uint8_t *source, uint32_t source_length,
    uint8_t *output, uint32_t output_capacity, uint32_t *output_length,
    hy_metrics *metrics, hy_cancel_fn cancel, void *cancel_data,
    char *error, uint32_t error_capacity);
HY_API void hy_close(hy_handle *handle);
#ifdef HY_ENABLE_AUDIT
/* Isolated fixture-only export; shared source encoder, never performs decode. */
HY_API int32_t hy_audit(hy_handle *handle, const uint8_t *source, uint32_t source_length,
    hy_metrics *metrics, char *error, uint32_t error_capacity);
#endif
#ifdef __cplusplus
}
#endif
#endif
