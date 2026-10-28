#include "index_adapter.h"
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
constexpr uint32_t ABI=1, MAX_SOURCE=8192, MAX_OUTPUT=8192, CONTEXT=4096, NEW_TOKENS=1024;
#include "index_template.h"
// Only this fixed native enum selects trusted instruction text. Source bytes
// are tokenized separately and never supply a language name or prompt fragment.
const char *target_language(uint32_t direction) {
 switch(direction){
  case INDEX_ZH_JA:return "日语";
  case INDEX_ZH_KO:return "韩语";
  case INDEX_ZH_FR:return "法语";
  case INDEX_ZH_ES:return "西班牙语";
  case INDEX_ZH_DE:return "德语";
  case INDEX_ZH_PT:return "葡萄牙语";
  case INDEX_ZH_EN:return "英语";
  case INDEX_JA_ZH:case INDEX_KO_ZH:case INDEX_FR_ZH:case INDEX_ES_ZH:case INDEX_DE_ZH:case INDEX_PT_ZH:case INDEX_EN_ZH:return "中文";
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
// Unicode White_Space, without locale-dependent classification. Cc controls
// (including Python's additional U+001C..001F whitespace) are rejected below.
bool term_whitespace(uint32_t c) {
 return c==0x20||c==0x85||c==0xa0||c==0x1680||(c>=0x2000&&c<=0x200a)||c==0x2028||c==0x2029||c==0x202f||c==0x205f||c==0x3000;
}
void validate_term(const uint8_t *p,uint32_t len) {
 require(p&&len&&len<=128,INDEX_INVALID,"term must be 1..128 UTF-8 bytes");
 std::string s(reinterpret_cast<const char*>(p),len);
 require(utf8(s),INDEX_INVALID,"term is not strict UTF-8");
 for(size_t i=0;i<s.size();){
  const size_t start=i;auto c=static_cast<unsigned char>(s[i++]);uint32_t value=c;
  if(c>=0x80){
   uint32_t remaining=c<0xe0?1:c<0xf0?2:3;value=c&((1u<<(6-remaining))-1);
   while(remaining--){value=(value<<6)|(static_cast<unsigned char>(s[i++])&63);}
  }
  require(value>0x1f&&(value<0x7f||value>0x9f),INDEX_INVALID,"term contains a control character");
  require((start!=0&&i!=s.size())||!term_whitespace(value),INDEX_INVALID,"term has surrounding whitespace");
 }
}
void validate_terms(const index_term *terms,uint32_t count) {
 require(terms&&count&&count<=64,INDEX_INVALID,"glossary must contain 1..64 term pairs");
 uint32_t total=0;
 for(uint32_t i=0;i<count;++i){
  const auto &term=terms[i];validate_term(term.source_term,term.source_length);validate_term(term.target_term,term.target_length);
  total+=term.source_length+term.target_length;
  require(total<=8192,INDEX_INVALID,"glossary exceeds 8192 UTF-8 bytes");
 }
}
std::vector<llama_token> tokenize(const llama_vocab *v,const std::string &s,bool special){
 std::vector<llama_token> out(s.size()+32);
 int32_t n=llama_tokenize(v,s.data(),static_cast<int32_t>(s.size()),out.data(),static_cast<int32_t>(out.size()),false,special);
 require(n>=0,INDEX_LITERAL_UNSAFE,"tokenization exceeded bounded buffer");out.resize(n);return out;
}
std::string detokenize(const llama_vocab *v,const std::vector<llama_token> &t,uint32_t cap){
 std::string out(cap,'\0');int32_t n=llama_detokenize(v,t.data(),static_cast<int32_t>(t.size()),out.data(),static_cast<int32_t>(cap),false,false);
 require(n>=0,INDEX_OUTPUT_LIMIT,"detokenization exceeded byte cap");out.resize(n);return out;
}
void init_metrics(index_metrics *m){
 require(m&&m->struct_size==sizeof(index_metrics)&&m->abi_version==ABI,INDEX_INVALID,"metrics ABI mismatch");
 *m={};m->struct_size=sizeof(index_metrics);m->abi_version=ABI;m->eog_token=-1;
}
}
struct index_handle {
 llama_model *model=nullptr;llama_context *ctx=nullptr;const llama_vocab *vocab=nullptr;
 std::vector<std::string> reserved;std::set<llama_token> reserved_ids;uint32_t user_defined=0;
 std::vector<llama_token> prefix_control,suffix_control;
 ~index_handle(){if(ctx)llama_free(ctx);if(model)llama_model_free(model);}
 bool forbidden(llama_token t)const {
  if(t<0||t>=llama_vocab_n_tokens(vocab))return true;
  auto a=llama_vocab_get_attr(vocab,t);
  return (a&(LLAMA_TOKEN_ATTR_UNKNOWN|LLAMA_TOKEN_ATTR_UNUSED|LLAMA_TOKEN_ATTR_CONTROL|LLAMA_TOKEN_ATTR_USER_DEFINED))||llama_vocab_is_eog(vocab,t)||reserved_ids.count(t);
 }
};
namespace {
std::vector<llama_token> source_tokens(index_handle *h,const uint8_t *p,uint32_t len,index_metrics *m){
 m->roundtrip_ok=0;
 require(h&&p&&len&&len<=MAX_SOURCE,INDEX_INVALID,"source must be 1..8192 UTF-8 bytes");
 std::string original(reinterpret_cast<const char*>(p),len);
 require(utf8(original),INDEX_INVALID,"source is not strict UTF-8");
 require(original.find('\0')==std::string::npos,INDEX_INVALID,"source contains NUL");
 const std::string &s=original;
 std::set<size_t> cuts{0,s.size()};
 for(const auto &word:h->reserved){
  for(size_t pos=0;(pos=s.find(word,pos))!=std::string::npos;++pos){
   // Fixed reserved spellings start with ASCII; this cut is a UTF-8 character boundary.
   require(word.size()>1&&static_cast<unsigned char>(word[0])<0x80,INDEX_IDENTITY,"unreviewed reserved spelling");
   cuts.insert(pos+1);
  }
 }
 std::vector<llama_token> tokens;size_t prev=0;
 for(auto end:cuts){if(end==0)continue;auto part=tokenize(h->vocab,s.substr(prev,end-prev),false);tokens.insert(tokens.end(),part.begin(),part.end());prev=end;}
 m->split_count+=static_cast<uint32_t>(cuts.size()-2);m->source_tokens+=static_cast<uint32_t>(tokens.size());
 m->reserved_spelling_count=static_cast<uint32_t>(h->reserved.size());m->user_defined_vocab_count=h->user_defined;
 for(auto t:tokens)if(h->forbidden(t))++m->forbidden_source_tokens;
 require(m->forbidden_source_tokens==0,INDEX_LITERAL_UNSAFE,"source produced a reserved token ID");
 require(detokenize(h->vocab,tokens,MAX_SOURCE)==s,INDEX_LITERAL_UNSAFE,"source byte roundtrip failed");m->roundtrip_ok=1;
 return tokens;
}
struct decode_control {index_cancel_fn callback;void *data;index_metrics *metrics;};
bool decode_abort(void *p){auto *d=static_cast<decode_control*>(p);++d->metrics->decode_callbacks;return d->callback&&d->callback(d->data)!=0;}
struct reset_guard {index_handle *h;~reset_guard(){llama_set_abort_callback(h->ctx,nullptr,nullptr);llama_memory_clear(llama_get_memory(h->ctx),true);}};
int32_t caught(char *e,uint32_t cap) noexcept {try{throw;}catch(const failure &x){error_text(e,cap,x.what());return x.code;}catch(const std::bad_alloc &){error_text(e,cap,"allocation failed");return INDEX_RESOURCE;}catch(const std::exception &){error_text(e,cap,"internal native exception");return INDEX_INTERNAL;}catch(...){error_text(e,cap,"unknown native exception");return INDEX_INTERNAL;}}
}
extern "C" INDEX_API int32_t index_open_v1(const char *cpu_backend_path,const char *model_path,index_handle **out,char *e,uint32_t cap){
 if(out){*out=nullptr;}
 error_text(e,cap,"");bool owned=false,backend_started=false;
 try{
  require(out&&cpu_backend_path&&model_path,INDEX_INVALID,"missing open argument");bool expected=false;require(live.compare_exchange_strong(expected,true),INDEX_INVALID,"one live model per process only");owned=true;
  std::unique_ptr<index_handle> h(new index_handle);
  require(ggml_backend_load(cpu_backend_path)!=nullptr,INDEX_RESOURCE,"verified CPU backend load failed");llama_backend_init();backend_started=true;
  ggml_backend_dev_t devices[]={nullptr};auto mp=llama_model_default_params();mp.devices=devices;mp.n_gpu_layers=0;mp.load_mode=LLAMA_LOAD_MODE_MMAP;mp.use_extra_bufts=false;mp.check_tensors=false;mp.load_mtp=false;
  h->model=llama_model_load_from_file(model_path,mp);require(h->model,INDEX_RESOURCE,"model load failed");h->vocab=llama_model_get_vocab(h->model);
  const char *actual=llama_model_chat_template(h->model,nullptr);require(actual&&std::strcmp(actual,TEMPLATE)==0,INDEX_IDENTITY,"embedded chat template mismatch");
  char architecture[32]={};
  require(llama_model_meta_val_str(h->model,"general.architecture",architecture,sizeof(architecture))==6&&std::strcmp(architecture,"qwen35")==0&&llama_model_is_hybrid(h->model),INDEX_IDENTITY,"model architecture mismatch");
  require(llama_vocab_n_tokens(h->vocab)==248320&&llama_vocab_bos(h->vocab)==11&&llama_vocab_eos(h->vocab)==248044&&llama_vocab_eot(h->vocab)==248046&&llama_vocab_pad(h->vocab)==248044&&!llama_vocab_get_add_bos(h->vocab)&&!llama_vocab_get_add_eos(h->vocab),INDEX_IDENTITY,"vocabulary identity mismatch");
  uint32_t controls=0,unused=0;
  for(int32_t i=0;i<llama_vocab_n_tokens(h->vocab);++i){
   auto a=llama_vocab_get_attr(h->vocab,i);
   if(a&LLAMA_TOKEN_ATTR_USER_DEFINED)++h->user_defined;
   if(a&LLAMA_TOKEN_ATTR_CONTROL)++controls;
   if(a&LLAMA_TOKEN_ATTR_UNUSED)++unused;
   if(h->forbidden(i)){h->reserved_ids.insert(i);const char *s=llama_vocab_get_text(h->vocab,i);if(s&&*s)h->reserved.emplace_back(s);}
  }
  // BOS=11 is llama's fallback and is the ordinary comma token. add_bos=false:
  // it is not a control and must never be added to the forbidden inventory.
  require(h->user_defined==6&&controls==27&&unused==243&&h->reserved_ids.size()==276,INDEX_IDENTITY,"special token inventory mismatch");
  require(llama_vocab_get_attr(h->vocab,11)==LLAMA_TOKEN_ATTR_NORMAL&&std::strcmp(llama_vocab_get_text(h->vocab,11),",")==0,INDEX_IDENTITY,"BOS fallback changed");
  h->prefix_control=tokenize(h->vocab,"<|im_start|>user\n",true);
  h->suffix_control=tokenize(h->vocab,"<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n",true);
  require(h->prefix_control==std::vector<llama_token>({248045,846,198})&&h->suffix_control==std::vector<llama_token>({248046,198,248045,74455,198,248068,271,248069,271}),INDEX_IDENTITY,"trusted control token shape mismatch");
  auto cp=llama_context_default_params();cp.n_ctx=CONTEXT;cp.n_batch=64;cp.n_ubatch=32;cp.n_seq_max=1;cp.n_threads=2;cp.n_threads_batch=2;cp.type_k=GGML_TYPE_F16;cp.type_v=GGML_TYPE_F16;cp.offload_kqv=false;cp.op_offload=false;cp.no_perf=false;
  h->ctx=llama_init_from_model(h->model,cp);require(h->ctx,INDEX_RESOURCE,"context creation failed");require(llama_n_ctx(h->ctx)==CONTEXT,INDEX_IDENTITY,"context size mismatch");*out=h.release();return INDEX_OK;
 }catch(...){if(backend_started)llama_backend_free();if(owned)live=false;return caught(e,cap);}
}
extern "C" INDEX_API int32_t index_translate_v1(index_handle *h,uint32_t dir,uint32_t style,uint32_t layout,const index_term *terms,uint32_t term_count,const uint8_t *p,uint32_t len,uint8_t *out,uint32_t outcap,uint32_t *outlen,index_metrics *m,index_cancel_fn cancel,void *data,char *e,uint32_t cap){
 if(outlen)*outlen=0;
 error_text(e,cap,"");
 try{
  init_metrics(m);
  require(dir>=INDEX_ZH_JA&&dir<=INDEX_EN_ZH&&style<=INDEX_FORMAL&&!(layout&~7u),INDEX_INVALID,"unsupported translation requirements");
  if(term_count)validate_terms(terms,term_count);
  const char *target=target_language(dir);
  require(h&&out&&outlen&&outcap>=MAX_OUTPUT&&target,INDEX_INVALID,"translate argument mismatch");
  require(!cancel||!cancel(data),INDEX_CANCELLED,"cancelled or deadline reached");
  llama_memory_clear(llama_get_memory(h->ctx),true);reset_guard reset{h};
  auto body=source_tokens(h,p,len,m);auto prompt=h->prefix_control;
  auto append=[&](const std::vector<llama_token> &tokens){prompt.insert(prompt.end(),tokens.begin(),tokens.end());};
  auto trusted=[&](const std::string &text){append(tokenize(h->vocab,text,false));};
  if(!style&&!layout&&!term_count){
   trusted(std::string("请将以下文本翻译为")+target+"，直接输出翻译结果，不要进行任何解释。\n\n");append(body);
  }else{
   trusted(std::string("请将以下文本翻译成")+target+"，并且严格遵循所有约束要求。\n\n【源文】\n");append(body);
   trusted("\n\n【约束要求】\n");uint32_t number=0;
   auto requirement=[&](const std::string &text){trusted(std::to_string(++number)+". "+text+"\n");};
   if(layout&INDEX_LITERALS)requirement("【硬性要求】原文中的网址、电子邮件地址、提及标记、问题编号和表情符号必须逐字保留，顺序和数量不变；无需翻译的数字、标点片段及受保护内容周围的空白也须逐字保留。");
   if(layout&INDEX_LINEBREAKS)requirement("【硬性要求】原文中的所有换行分隔符必须逐字保留，顺序和数量不变，包括 CRLF、LF 和 CR。");
   if(layout&INDEX_EDGE_WHITESPACE)requirement("【硬性要求】原文开头、结尾以及受保护内容和无需翻译片段周围的所有空白字符必须逐字保留。");
   if(term_count){
    trusted(std::to_string(++number)+". 【硬性要求】专名/术语对照: ");
    for(uint32_t i=0;i<term_count;++i){
     if(i)trusted("、");
     append(source_tokens(h,terms[i].source_term,terms[i].source_length,m));trusted("→");
     append(source_tokens(h,terms[i].target_term,terms[i].target_length,m));
    }
    trusted("\n");
   }
   if(style)requirement(style==INDEX_NATURAL?"【注意】使用自然、地道的表达，保留原文的语气和礼貌程度。":"【注意】使用正式、得体的表达。");
   trusted("\n只输出译文，不要有任何额外说明。");
  }
  append(h->suffix_control);m->prompt_tokens=static_cast<uint32_t>(prompt.size());
  require(prompt.size()+NEW_TOKENS<=CONTEXT,INDEX_CONTEXT_LIMIT,"prompt plus reserved generation exceeds context");
  // Greedy is temperature0/top_p1/top_k0/min_p0/repetition1, no other sampler.
  std::unique_ptr<llama_sampler,decltype(&llama_sampler_free)> sampler(llama_sampler_init_greedy(),llama_sampler_free);
  require(bool(sampler),INDEX_RESOURCE,"sampler allocation failed");
  decode_control control{cancel,data,m};llama_set_abort_callback(h->ctx,decode_abort,&control);
  auto decode=[&](llama_token *tokens,int32_t count){
   if(cancel&&cancel(data))throw failure(INDEX_CANCELLED,"cancelled or deadline reached");
   ++m->decode_calls;m->last_decode_code=llama_decode(h->ctx,llama_batch_get_one(tokens,count));
   if(m->last_decode_code==2)throw failure(INDEX_CANCELLED,"decode aborted");
   require(m->last_decode_code==0,INDEX_DECODE_ERROR,"decode failed");
  };
  for(size_t pos=0;pos<prompt.size();pos+=64)decode(prompt.data()+pos,static_cast<int32_t>(std::min<size_t>(64,prompt.size()-pos)));
  std::vector<llama_token> generated;std::string bytes;
  for(uint32_t n=0;n<NEW_TOKENS;++n){
   if(cancel&&cancel(data))throw failure(INDEX_CANCELLED,"cancelled or deadline reached");
   auto token=llama_sampler_sample(sampler.get(),h->ctx,-1);
   require(token>=0&&token<llama_vocab_n_tokens(h->vocab),INDEX_DECODE_ERROR,"invalid sampled token");
   if(token==248044||token==248046){
    require(llama_vocab_is_eog(h->vocab,token),INDEX_IDENTITY,"fixed completion token is not EOG");
    m->eog_token=token;std::string result=detokenize(h->vocab,generated,MAX_OUTPUT);
    require(result==bytes&&utf8(result)&&!result.empty(),INDEX_LITERAL_UNSAFE,"output byte roundtrip or strict UTF-8 failed");
    std::memcpy(out,result.data(),result.size());*outlen=static_cast<uint32_t>(result.size());return INDEX_OK;
   }
   // b11913 also calls FIM_PAD/REPO_NAME/FILE_SEP EOG. They are not the
   // generation_config EOS set and cannot turn reserved-token output green.
   require(!h->forbidden(token),INDEX_LITERAL_UNSAFE,"generated reserved token");
   char piece[MAX_OUTPUT];int32_t count=llama_token_to_piece(h->vocab,token,piece,MAX_OUTPUT,0,false);
   require(count>=0&&bytes.size()+count<=MAX_OUTPUT,INDEX_OUTPUT_LIMIT,"output byte cap reached");
   bytes.append(piece,count);generated.push_back(token);m->output_tokens=static_cast<uint32_t>(generated.size());decode(&token,1);
  }
  throw failure(INDEX_OUTPUT_LIMIT,"token cap reached without real EOG");
 }catch(...){return caught(e,cap);}
}
extern "C" INDEX_API void index_close(index_handle *h){if(h){try{delete h;llama_backend_free();}catch(...){}live=false;}}
