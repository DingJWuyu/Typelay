#include "hy_adapter.h"
#include "llama.h"
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
constexpr uint32_t ABI=3, MAX_SOURCE=8192, MAX_OUTPUT=8192, CONTEXT=1024, NEW_TOKENS=192;
constexpr const char *TEMPLATE=R"HY({% if messages[0]['role'] == 'system' %}{% set loop_messages = messages[1:] %}{% set system_message = messages[0]['content'] %}<｜hy_begin▁of▁sentence｜>{{ system_message }}<｜hy_place▁holder▁no▁3｜>{% else %}{% set loop_messages = messages %}<｜hy_begin▁of▁sentence｜>{% endif %}{% for message in loop_messages %}{% if message['role'] == 'user' %}<｜hy_User｜>{{ message['content'] }}{% elif message['role'] == 'assistant' %}<｜hy_Assistant｜>{{ message['content'] }}<｜hy_place▁holder▁no▁2｜>{% endif %}{% endfor %}{% if add_generation_prompt %}<｜hy_Assistant｜>{% else %}<｜hy_place▁holder▁no▁8｜>{% endif %})HY";
// Only this fixed native enum selects trusted instruction text. Source bytes
// are tokenized separately and never supply a language name or prompt fragment.
const char *target_language(uint32_t direction) {
 switch(direction){
  case HY_ZH_JA:return "日语";
  case HY_ZH_KO:return "韩语";
  case HY_ZH_FR:return "法语";
  case HY_ZH_ES:return "西班牙语";
  case HY_ZH_DE:return "德语";
  case HY_ZH_PT:return "葡萄牙语";
  case HY_JA_ZH:case HY_KO_ZH:case HY_FR_ZH:case HY_ES_ZH:case HY_DE_ZH:case HY_PT_ZH:return "中文";
  default:return nullptr;
 }
}
std::atomic<bool> live{false};
struct failure : std::runtime_error {int32_t code; failure(int32_t c,const char *m):std::runtime_error(m),code(c){}};
void require(bool ok,int32_t c,const char *m){if(!ok)throw failure(c,m);}
void error_text(char *p,uint32_t cap,const char *text) noexcept {
 if(p&&cap){size_t n=std::min<size_t>(cap-1,std::strlen(text));std::memcpy(p,text,n);p[n]=0;}
}
bool utf8(const std::string &s) {
 size_t i=0;while(i<s.size()){
  auto c=static_cast<unsigned char>(s[i++]);if(c<0x80)continue;
  uint32_t n=0,v=0,min=0;
  if(c>=0xc2&&c<=0xdf){n=1;v=c&31;min=0x80;}
  else if(c>=0xe0&&c<=0xef){n=2;v=c&15;min=0x800;}
  else if(c>=0xf0&&c<=0xf4){n=3;v=c&7;min=0x10000;}else return false;
  if(s.size()-i<n)return false;
  while(n--){auto b=static_cast<unsigned char>(s[i++]);if((b&0xc0)!=0x80)return false;v=(v<<6)|(b&63);}
  if(v<min||v>0x10ffff||(v>=0xd800&&v<=0xdfff))return false;
 }return true;
}
std::vector<llama_token> tokenize(const llama_vocab *v,const std::string &s,bool special){
 std::vector<llama_token> out(s.size()+32);
 int32_t n=llama_tokenize(v,s.data(),static_cast<int32_t>(s.size()),out.data(),static_cast<int32_t>(out.size()),false,special);
 require(n>=0,HY_LITERAL_UNSAFE,"tokenization exceeded bounded buffer");out.resize(n);return out;
}
std::string detokenize(const llama_vocab *v,const std::vector<llama_token> &t,uint32_t cap){
 std::string out(cap,'\0');int32_t n=llama_detokenize(v,t.data(),static_cast<int32_t>(t.size()),out.data(),static_cast<int32_t>(cap),false,false);
 require(n>=0,HY_OUTPUT_LIMIT,"detokenization exceeded byte cap");out.resize(n);return out;
}
void init_metrics(hy_metrics *m){
 require(m&&m->struct_size==sizeof(hy_metrics)&&m->abi_version==ABI,HY_INVALID,"metrics ABI mismatch");
 *m={};m->struct_size=sizeof(hy_metrics);m->abi_version=ABI;m->eog_token=-1;
}
}
struct hy_handle {
 llama_model *model=nullptr;llama_context *ctx=nullptr;const llama_vocab *vocab=nullptr;
 std::vector<std::string> reserved;std::set<llama_token> reserved_ids;uint32_t user_defined=0;
 std::vector<llama_token> prefix_control,suffix_control;
 ~hy_handle(){if(ctx)llama_free(ctx);if(model)llama_model_free(model);}
 bool forbidden(llama_token t)const {
  if(t<0||t>=llama_vocab_n_tokens(vocab))return true;
  auto a=llama_vocab_get_attr(vocab,t);
  return (a&(LLAMA_TOKEN_ATTR_UNKNOWN|LLAMA_TOKEN_ATTR_UNUSED|LLAMA_TOKEN_ATTR_CONTROL|LLAMA_TOKEN_ATTR_USER_DEFINED))||llama_vocab_is_eog(vocab,t)||reserved_ids.count(t);
 }
};
namespace {
std::vector<llama_token> source_tokens(hy_handle *h,const uint8_t *p,uint32_t len,hy_metrics *m){
 require(h&&p&&len&&len<=MAX_SOURCE,HY_INVALID,"source must be 1..8192 UTF-8 bytes");
 std::string original(reinterpret_cast<const char*>(p),len);
 require(utf8(original),HY_INVALID,"source is not strict UTF-8");
 // Explicit TypeLay stage1 canonical policy, NOT an upstream normalizer rule.
 // Preserve the caller's original byte buffer; derive only CRLF -> LF.
 std::string s;s.reserve(original.size());
 for(size_t i=0;i<original.size();++i){
  if(original[i]=='\r'){
   require(i+1<original.size()&&original[i+1]=='\n',HY_LITERAL_UNSAFE,"standalone CR is not supported");
   s.push_back('\n');++i;
  }else{s.push_back(original[i]);}
 }
 std::set<size_t> cuts{0,s.size()};
 for(const auto &word:h->reserved){
  for(size_t pos=0;(pos=s.find(word,pos))!=std::string::npos;++pos){
   // Fixed reserved spellings start with ASCII; this cut is a UTF-8 character boundary.
   require(word.size()>1&&static_cast<unsigned char>(word[0])<0x80,HY_IDENTITY,"unreviewed reserved spelling");
   cuts.insert(pos+1);
  }
 }
 std::vector<llama_token> tokens;size_t prev=0;
 for(auto end:cuts){if(end==0)continue;auto part=tokenize(h->vocab,s.substr(prev,end-prev),false);tokens.insert(tokens.end(),part.begin(),part.end());prev=end;}
 m->split_count=static_cast<uint32_t>(cuts.size()-2);m->source_tokens=static_cast<uint32_t>(tokens.size());
 m->reserved_spelling_count=static_cast<uint32_t>(h->reserved.size());m->user_defined_vocab_count=h->user_defined;
 for(auto t:tokens)if(h->forbidden(t))++m->forbidden_source_tokens;
 require(m->forbidden_source_tokens==0,HY_LITERAL_UNSAFE,"source produced a reserved token ID");
 require(detokenize(h->vocab,tokens,MAX_SOURCE)==s,HY_LITERAL_UNSAFE,"canonical source byte roundtrip failed");m->roundtrip_ok=1;
 return tokens;
}
struct decode_control {hy_cancel_fn callback;void *data;hy_metrics *metrics;};
bool decode_abort(void *p){auto *d=static_cast<decode_control*>(p);++d->metrics->decode_callbacks;return d->callback&&d->callback(d->data)!=0;}
struct reset_guard {hy_handle *h;~reset_guard(){llama_set_abort_callback(h->ctx,nullptr,nullptr);llama_memory_clear(llama_get_memory(h->ctx),true);}};
int32_t caught(char *e,uint32_t cap) noexcept {try{throw;}catch(const failure &x){error_text(e,cap,x.what());return x.code;}catch(const std::bad_alloc &){error_text(e,cap,"allocation failed");return HY_RESOURCE;}catch(const std::exception &){error_text(e,cap,"internal native exception");return HY_INTERNAL;}catch(...){error_text(e,cap,"unknown native exception");return HY_INTERNAL;}}
}
extern "C" HY_API int32_t hy_open_v3(const char *cpu_backend_path,const char *model_path,hy_handle **out,char *e,uint32_t cap){
 if(out){*out=nullptr;}
 error_text(e,cap,"");bool owned=false;
 try{
  require(out&&cpu_backend_path&&model_path,HY_INVALID,"missing open argument");bool expected=false;require(live.compare_exchange_strong(expected,true),HY_INVALID,"one live model per process only");owned=true;
  std::unique_ptr<hy_handle> h(new hy_handle);
  require(ggml_backend_load(cpu_backend_path)!=nullptr,HY_RESOURCE,"verified CPU backend load failed");llama_backend_init();
  ggml_backend_dev_t devices[]={nullptr};auto mp=llama_model_default_params();mp.devices=devices;mp.n_gpu_layers=0;mp.load_mode=LLAMA_LOAD_MODE_MMAP;mp.use_extra_bufts=false;mp.check_tensors=false;mp.load_mtp=false;
  h->model=llama_model_load_from_file(model_path,mp);require(h->model,HY_RESOURCE,"model load failed");h->vocab=llama_model_get_vocab(h->model);
  const char *actual=llama_model_chat_template(h->model,nullptr);require(actual&&std::strcmp(actual,TEMPLATE)==0,HY_IDENTITY,"embedded chat template mismatch");
  require(llama_vocab_n_tokens(h->vocab)==120818&&llama_vocab_bos(h->vocab)==120000&&llama_vocab_eos(h->vocab)==120020,HY_IDENTITY,"vocabulary identity mismatch");
  for(int32_t i=0;i<llama_vocab_n_tokens(h->vocab);++i){auto a=llama_vocab_get_attr(h->vocab,i);if(a&LLAMA_TOKEN_ATTR_USER_DEFINED)++h->user_defined;if(h->forbidden(i)){h->reserved_ids.insert(i);const char *s=llama_vocab_get_text(h->vocab,i);if(s&&*s)h->reserved.emplace_back(s);}}
  h->reserved_ids.insert(llama_vocab_bos(h->vocab));h->reserved_ids.insert(llama_vocab_eos(h->vocab));h->reserved_ids.insert(llama_vocab_eot(h->vocab));h->reserved_ids.insert(llama_vocab_pad(h->vocab));h->reserved_ids.insert(llama_vocab_sep(h->vocab));
  require(h->user_defined==15,HY_IDENTITY,"USER_DEFINED inventory changed");
  h->prefix_control=tokenize(h->vocab,"<｜hy_begin▁of▁sentence｜><｜hy_User｜>",true);h->suffix_control=tokenize(h->vocab,"<｜hy_Assistant｜>",true);
  require(h->prefix_control==std::vector<llama_token>({120000,120006})&&h->suffix_control==std::vector<llama_token>({120007}),HY_IDENTITY,"trusted control token shape mismatch");
  auto cp=llama_context_default_params();cp.n_ctx=CONTEXT;cp.n_batch=64;cp.n_ubatch=32;cp.n_seq_max=1;cp.n_threads=2;cp.n_threads_batch=2;cp.type_k=GGML_TYPE_F16;cp.type_v=GGML_TYPE_F16;cp.offload_kqv=false;cp.op_offload=false;cp.no_perf=false;
  h->ctx=llama_init_from_model(h->model,cp);require(h->ctx,HY_RESOURCE,"context creation failed");require(llama_n_ctx(h->ctx)==CONTEXT,HY_IDENTITY,"context size mismatch");*out=h.release();return HY_OK;
 }catch(...){if(owned)live=false;return caught(e,cap);}
}
extern "C" HY_API int32_t hy_translate(hy_handle *h,uint32_t dir,const uint8_t *p,uint32_t len,uint8_t *out,uint32_t outcap,uint32_t *outlen,hy_metrics *m,hy_cancel_fn cancel,void *data,char *e,uint32_t cap){
 if(outlen){*outlen=0;}
 error_text(e,cap,"");
 try{
  init_metrics(m);const char *target=target_language(dir);require(h&&out&&outlen&&outcap>=MAX_OUTPUT&&target,HY_INVALID,"translate argument mismatch");
  llama_memory_clear(llama_get_memory(h->ctx),true);reset_guard reset{h};auto body=source_tokens(h,p,len,m);
  std::string instruction=std::string("将以下文本翻译为")+target+"，注意只需要输出翻译后的结果，不要额外解释：\n\n";
  auto prompt=h->prefix_control;auto ins=tokenize(h->vocab,instruction,false);prompt.insert(prompt.end(),ins.begin(),ins.end());prompt.insert(prompt.end(),body.begin(),body.end());prompt.insert(prompt.end(),h->suffix_control.begin(),h->suffix_control.end());m->prompt_tokens=static_cast<uint32_t>(prompt.size());
  require(prompt.size()+NEW_TOKENS<=CONTEXT,HY_CONTEXT_LIMIT,"prompt plus reserved generation exceeds context");
  std::unique_ptr<llama_sampler,decltype(&llama_sampler_free)> sampler(llama_sampler_init_greedy(),llama_sampler_free);require(bool(sampler),HY_RESOURCE,"sampler allocation failed");
  decode_control control{cancel,data,m};llama_set_abort_callback(h->ctx,decode_abort,&control);
  auto decode=[&](llama_token *tokens,int32_t count){
   if(cancel&&cancel(data))throw failure(HY_CANCELLED,"cancelled or deadline reached");
   ++m->decode_calls;m->last_decode_code=llama_decode(h->ctx,llama_batch_get_one(tokens,count));
   if(m->last_decode_code==2){throw failure(HY_CANCELLED,"decode aborted");}
   require(m->last_decode_code==0,HY_DECODE_ERROR,"decode failed");
  };
  for(size_t pos=0;pos<prompt.size();pos+=64)decode(prompt.data()+pos,static_cast<int32_t>(std::min<size_t>(64,prompt.size()-pos)));
  std::vector<llama_token> generated;std::string bytes;
  for(uint32_t n=0;n<NEW_TOKENS;++n){
   if(cancel&&cancel(data))throw failure(HY_CANCELLED,"cancelled or deadline reached");
   auto token=llama_sampler_sample(sampler.get(),h->ctx,-1);
   require(token>=0&&token<llama_vocab_n_tokens(h->vocab),HY_DECODE_ERROR,"invalid sampled token");
   if(llama_vocab_is_eog(h->vocab,token)){
    m->eog_token=token;std::string result=detokenize(h->vocab,generated,MAX_OUTPUT);require(result==bytes&&utf8(result)&&!result.empty(),HY_LITERAL_UNSAFE,"output byte roundtrip or strict UTF-8 failed");
    std::memcpy(out,result.data(),result.size());*outlen=static_cast<uint32_t>(result.size());return HY_OK;
   }
   require(!h->forbidden(token),HY_LITERAL_UNSAFE,"generated non-EOG reserved token");
   char piece[MAX_OUTPUT];int32_t count=llama_token_to_piece(h->vocab,token,piece,MAX_OUTPUT,0,false);require(count>=0&&bytes.size()+count<=MAX_OUTPUT,HY_OUTPUT_LIMIT,"output byte cap reached");bytes.append(piece,count);generated.push_back(token);m->output_tokens=static_cast<uint32_t>(generated.size());decode(&token,1);
  }
  throw failure(HY_OUTPUT_LIMIT,"token cap reached without real EOG");
 }catch(...){return caught(e,cap);}
}
extern "C" HY_API void hy_close(hy_handle *h){if(h){try{delete h;llama_backend_free();}catch(...){}live=false;}}
#ifdef HY_ENABLE_AUDIT
extern "C" HY_API int32_t hy_audit(hy_handle *h,const uint8_t *p,uint32_t len,hy_metrics *m,char *e,uint32_t cap){error_text(e,cap,"");try{init_metrics(m);source_tokens(h,p,len,m);return HY_OK;}catch(...){return caught(e,cap);}}
#endif
