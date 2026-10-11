"""One-time additions to the reviewed migration, removed before committing source."""
from pathlib import Path
import re
import shutil

root = Path.cwd()
def rewrite(path, old, new):
    file = root / path
    text = file.read_text(encoding='utf-8')
    assert old in text, (path, old)
    file.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

# Policy and UTF-16 mapping are domain decisions, not wire serialization.
for name in ('FieldPolicy.cs', 'TextMapping.cs'):
    source = root / 'src/TypeLay.Protocol' / name
    target = root / 'src/TypeLay.Core' / name
    target.write_text(source.read_text(encoding='utf-8').replace('namespace TypeLay.Protocol;', 'namespace TypeLay.Core;'), encoding='utf-8', newline='\n')
    source.unlink()

path = root / 'tests/contracts/TypeLay.Protocol.Tests/ProtocolContractTests.cs'
text = path.read_text(encoding='utf-8')
header, rest = text.split('    [Fact', 1)
blocks = re.split(r'(?=    \[Fact)', '    [Fact' + rest.rstrip()[:-1])
wire = ''.join(block for block in blocks if '[Fact(DisplayName = "wire-' in block)
selection = ''.join(block for block in blocks if '[Fact(DisplayName = "uia-' in block)
policy = ''.join(block for block in blocks if '[Fact(DisplayName = "policy-' in block)
assert len(re.findall(r'\[Fact', wire + selection + policy)) == 30
normal = next(line for line in header.splitlines() if 'FieldMetadata normal' in line) + '\n'
header = header.replace(normal, '')
path.write_text(header + wire + '}\n', encoding='utf-8', newline='\n')

rejected_path = root / 'tests/contracts/TypeLay.Protocol.Tests/RejectedInputTests.cs'
rejected = rejected_path.read_text(encoding='utf-8')
position = rejected.index('    [Theory]', rejected.index('    [Theory]') + 1)
sensitive = rejected[position:].rstrip()[:-1]
rejected_path.write_text(rejected[:position] + '}\n', encoding='utf-8', newline='\n')
common = 'using System.Runtime.CompilerServices;\nusing TypeLay.Core;\nusing Xunit;\nnamespace TypeLay.Core.Tests;\n'
check = '    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => Assert.True(value, expression);\n'
for name, members in [('SelectionMappingTests', selection), ('FieldPolicyTests', normal + policy + sensitive)]:
    (root / f'tests/behavior/TypeLay.Core.Tests/{name}.cs').write_text(common + f'public sealed class {name}\n{{\n' + check + members + '}\n', encoding='utf-8', newline='\n')
rewrite('docs/agents/architecture.md', '和展示状态的纯逻辑', '、字段安全策略、UTF-16 选区映射和展示状态的纯逻辑')
rewrite('docs/agents/architecture.md', '及 Messages 数据契约、字段策略、UTF-16 映射', '及 Messages 数据契约')

# Do not preserve obsolete executable tooling as a second apparent development path.
shutil.rmtree(root / 'docs/history/preview-tools')
for path in (root / 'docs/history/milestones').rglob('tools'):
    if path.is_dir():
        shutil.rmtree(path)
for path in (root / 'docs/history/milestones').rglob('.github'):
    if path.is_dir():
        shutil.rmtree(path)
rewrite('docs/history/AGENTS.md', 'preview-tools/ 是已退休工具；', '')
rewrite('docs/history/AGENTS.md', '不要运行旧 workflow、bootstrap、source-verifier 或旧发布脚本。', '旧 workflow、bootstrap、source-verifier 和发布脚本已删除，原内容可从 Git 历史恢复，不再作为另一套开发入口保留。')

rewrite('.editorconfig', 'csharp_new_line_before_open_brace = all', 'csharp_new_line_before_open_brace = all\ncsharp_preserve_single_line_statements = false\ncsharp_preserve_single_line_blocks = false')
bootstrap = root / 'packaging/windows/start.ps1'
bootstrap.write_text(bootstrap.read_text(encoding='utf-8').replace('TypeLay could not start. Extract the complete Windows preview archive into one folder, then retry. See README.md.', 'TypeLay 无法启动。请将完整 Windows 预览包解压到同一个文件夹后重试，详见 README.md。'), encoding='utf-8-sig', newline='\n')
rewrite('docs/agents/skills.md', '完整上游内容保留在 .agents/skills', '27 个正式技能及各自的参考文件保留在 .agents/skills')
rewrite('docs/agents/skills.md', '更新时明确选择 revision，通过安装器重建并检查 diff', '更新时明确选择 revision 与本文件列出的 27 个 skill 名称，通过安装器重建并检查 diff；不要用 --skill *，以免引入上游的实验或归档技能')

# xUnit cancellation flows through the real protocol API; do not suppress the analyzer.
for path in (root / 'tests/contracts/TypeLay.Protocol.Tests').glob('*Tests.cs'):
    text = path.read_text(encoding='utf-8')
    text = re.sub(r'Wire\.ReadAsync<([^>]+)>\(stream\)', r'Wire.ReadAsync<\1>(stream, TestContext.Current.CancellationToken)', text)
    text = re.sub(r'Wire\.WriteAsync\(stream, (request|response|snapshot)\)', r'Wire.WriteAsync(stream, \1, TestContext.Current.CancellationToken)', text)
    path.write_text(text, encoding='utf-8', newline='\n')
rewrite('README.md', '历史报告、日志和已退休工具', '历史报告和日志')
