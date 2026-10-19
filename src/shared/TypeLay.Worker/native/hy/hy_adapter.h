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
/* Fixed ABI 3: no upstream llama structs cross the boundary. Single-threaded owner. */
typedef struct hy_handle hy_handle;
typedef int32_t (*hy_cancel_fn)(void *user_data);
typedef struct hy_metrics {
    uint32_t struct_size, abi_version;
    uint32_t source_tokens, prompt_tokens, output_tokens, split_count;
    uint32_t roundtrip_ok, forbidden_source_tokens, reserved_spelling_count, user_defined_vocab_count;
    uint32_t decode_calls, decode_callbacks;
    int32_t eog_token, last_decode_code;
} hy_metrics;
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
HY_API int32_t hy_open_v3(const char *cpu_backend_path, const char *model_path,
    hy_handle **out, char *error, uint32_t error_capacity);
/* direction: exact hy_direction value. Fixed greedy, 192 new tokens, context 1024.
   ABI3 explicitly derives model canonical text by CRLF->LF only. Original
   source bytes stay caller-owned and are never modified. Bare CR is rejected.
   All other bytes must roundtrip exactly; roundtrip_ok refers to canonical text.
   Outer product formatting/writeback must keep the original source/layout.
   On every failure *output_length=0; no partial output is published.
   metrics must have struct_size=sizeof(hy_metrics), abi_version=3.
   cancel callback runs from CPU decode and may be NULL; callback must not throw.
   The owner must not call close or another operation concurrently. */
HY_API int32_t hy_translate(hy_handle *handle, uint32_t direction,
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
