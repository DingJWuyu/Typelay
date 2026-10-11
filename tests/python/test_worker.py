"""Deterministic protocol/structure tests. Does NOT substitute for actual model inference."""
from pathlib import Path
import hashlib
import io
import json
import struct
import sys
import tempfile
import unittest
import subprocess
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'worker'))
from typelay_worker.protocol import read_exact, read_frame, write_frame, valid_text, MAX_FRAME
from typelay_worker.engine import EngineError, LocalEngine, preserve_structure, merge_manual

class UppercaseSegment:
    """Test double for structure-helper tests only; never imported by the runtime."""
    def translate(self, text): return text.upper()

class ProtocolTests(unittest.TestCase):
    def test_round_trip_unicode(self):
        value={'id':1,'op':'translate','text':'你好🙂\r\nline'}
        stream=io.BytesIO(); write_frame(stream,value); stream.seek(0)
        self.assertEqual(read_frame(stream),value)
    def test_byte_order(self):
        stream=io.BytesIO(); write_frame(stream,{'id':1})
        self.assertEqual(struct.unpack('<i',stream.getvalue()[:4])[0],len(stream.getvalue())-4)
    def test_short_reads(self):
        class Partial(io.BytesIO):
            def read(self,n=-1): return super().read(min(n,1))
        self.assertEqual(read_exact(Partial(b'abc'),3),b'abc')
    def test_truncated(self):
        for data in (b'',b'abc',struct.pack('<i',8)+b'{}'):
            with self.subTest(data=data),self.assertRaises(EOFError): read_frame(io.BytesIO(data))
    def test_bad_sizes(self):
        for length in (-1,0,MAX_FRAME+1,2**31-1):
            with self.subTest(length=length),self.assertRaises(ValueError): read_frame(io.BytesIO(struct.pack('<i',length)))
    def test_nonobject(self):
        with self.assertRaises(ValueError): read_frame(io.BytesIO(struct.pack('<i',2)+b'[]'))
    def test_invalid_utf8(self):
        with self.assertRaises(UnicodeError): read_frame(io.BytesIO(struct.pack('<i',1)+b'\xff'))
    def test_invalid_json(self):
        with self.assertRaises(ValueError): read_frame(io.BytesIO(struct.pack('<i',1)+b'{'))
    def test_oversized_write(self):
        with self.assertRaises(ValueError): write_frame(io.BytesIO(),{'text':'x'*MAX_FRAME})
    def test_no_nan_write(self):
        with self.assertRaises(ValueError): write_frame(io.BytesIO(),{'value':float('nan')})
    def test_null_and_nontext(self):
        for value in (None,1,True,[],{},'a\0b'):
            with self.subTest(value=value): self.assertFalse(valid_text(value))
    def test_empty_policy(self):
        self.assertFalse(valid_text(' ')); self.assertTrue(valid_text('',empty=True))
    def test_utf16_limit(self):
        self.assertTrue(valid_text('🙂'*4096)); self.assertFalse(valid_text('🙂'*4097))
    def test_unpaired_surrogates(self):
        self.assertFalse(valid_text('\ud800')); self.assertFalse(valid_text('\udfff'))
    def test_two_frames(self):
        stream=io.BytesIO(); write_frame(stream,{'id':1}); write_frame(stream,{'id':2}); stream.seek(0)
        self.assertEqual(read_frame(stream)['id'],1); self.assertEqual(read_frame(stream)['id'],2)

class StructureTests(unittest.TestCase):
    def test_whitespace(self): self.assertEqual(preserve_structure('  hello  ',UppercaseSegment()),'  HELLO  ')
    def test_line_endings(self): self.assertEqual(preserve_structure('a\r\nb\nc\rd',UppercaseSegment()),'A\r\nB\nC\rD')
    def test_url(self): self.assertEqual(preserve_structure('go https://Example.com/a?b=1 now',UppercaseSegment()),'GO https://Example.com/a?b=1 NOW')
    def test_mention(self): self.assertEqual(preserve_structure('hello @Alice thanks',UppercaseSegment()),'HELLO @Alice THANKS')
    def test_email(self): self.assertEqual(preserve_structure('mail Me@Example.com please',UppercaseSegment()),'MAIL Me@Example.com PLEASE')
    def test_issue(self): self.assertEqual(preserve_structure('issue #123 ready',UppercaseSegment()),'ISSUE #123 READY')
    def test_emoji(self): self.assertEqual(preserve_structure('yes 👩‍💻 thanks 🙂',UppercaseSegment()),'YES 👩‍💻 THANKS 🙂')
    def test_numeric(self): self.assertEqual(preserve_structure('14:30 12345',UppercaseSegment()),'14:30 12345')
    def test_bad_output(self):
        class Broken:
            def translate(self,text): return '\ud800'
        with self.assertRaises(EngineError): preserve_structure('hello',Broken())
    def test_manual_exact_prefix(self): self.assertEqual(merge_manual(' My choice. ','Added.'),' My choice. Added.')
    def test_manual_space(self): self.assertEqual(merge_manual('Hello','World'),'Hello World')
    def test_manual_limit(self):
        with self.assertRaises(EngineError): merge_manual('x'*8191,'xx')
    def test_manual_invalid(self):
        with self.assertRaises(EngineError): merge_manual('','Hello')

class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(); self.root=Path(self.tmp.name); self.entries=[]
        for pair in ('zh_en','en_zh'):
            for name in ('model/model.bin','sentencepiece.model'):
                rel=f'{pair}/{name}'; path=self.root/rel; path.parent.mkdir(parents=True,exist_ok=True)
                path.write_bytes(b'inventory-test-bytes-not-a-model')
                self.entries.append({'path':rel,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
        self.save()
    def tearDown(self): self.tmp.cleanup()
    def save(self): (self.root/'installed.json').write_text(json.dumps({'schema':1,'files':self.entries}),encoding='utf8')
    def test_inventory_consistency_only(self): LocalEngine._verify_inventory(self.root)
    def test_tampered_file(self):
        (self.root/self.entries[0]['path']).write_bytes(b'changed')
        with self.assertRaises(EngineError): LocalEngine._verify_inventory(self.root)
    def test_path_escape(self):
        self.entries[0]['path']='../escape'; self.save()
        with self.assertRaises(EngineError): LocalEngine._verify_inventory(self.root)
    def test_duplicate(self):
        self.entries[0]=self.entries[1]; self.save()
        with self.assertRaises(EngineError): LocalEngine._verify_inventory(self.root)
    def test_missing_required(self):
        self.entries=self.entries[:3]; self.save()
        with self.assertRaises(EngineError): LocalEngine._verify_inventory(self.root)
    def test_no_inventory(self):
        (self.root/'installed.json').unlink()
        with self.assertRaises(EngineError): LocalEngine._verify_inventory(self.root)

class ProcessBoundaryTests(unittest.TestCase):
    def test_missing_model_fails_without_text_or_trace(self):
        with tempfile.TemporaryDirectory() as folder:
            script=Path(__file__).resolve().parents[2]/'worker'/'run_worker.py'
            result=subprocess.run([sys.executable,'-I','-B',str(script),'--models',folder],input=b'PRIVATE_SENTINEL',capture_output=True,timeout=10)
            self.assertEqual(result.returncode,2)
            self.assertEqual(result.stdout,b'')
            self.assertEqual(result.stderr,b'')
    def test_missing_model_selfcheck_reports_failure_only(self):
        with tempfile.TemporaryDirectory() as folder:
            script=Path(__file__).resolve().parents[2]/'worker'/'run_worker.py'
            result=subprocess.run([sys.executable,'-I','-B',str(script),'--models',folder,'--self-check'],capture_output=True,timeout=10)
            self.assertEqual(result.returncode,2)
            self.assertEqual(json.loads(result.stdout)['status'],'failed')
            self.assertNotIn(b'Traceback',result.stderr)

if __name__ == '__main__': unittest.main(verbosity=2)
