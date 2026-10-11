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

rewrite('src/TypeLay.Launcher/launcher.c', 'tools/bootstrap/build-launcher.py', 'eng/build-launcher.py')
rewrite('docs/agents/architecture.md', 'WriteSafety 、', 'WriteSafety、')
(root / 'docs/history/milestones/M1/workflow.yml').unlink()
# Active guidance describes the enduring workflow, not a narrative of this migration.
skills = root / 'docs/agents/skills.md'
text = skills.read_text(encoding='utf-8').split('\n## 本轮采用的工作纪律')[0].rstrip()
text = text.replace('用户本轮明确要求 setup 的配置目标已落实到根 AGENTS 的 Agent skills 区块及 issue-tracker/domain/triage-labels 文件', '项目配置位于根 AGENTS 的 Agent skills 区块及 issue-tracker/domain/triage-labels 文件')
skills.write_text(text + '\n', encoding='utf-8', newline='\n')
rewrite('tests/AGENTS.md', '真实帧序列化、字段策略和 UTF-16 范围映射', '真实帧序列化与非法输入拒绝')
rewrite('tests/AGENTS.md', '人工草稿和拒绝条件。', '人工草稿、字段策略、UTF-16 选区映射和拒绝条件。')
rewrite('src/TypeLay.Broker/Input/UiaInputProvider.cs',
        'AutomationProperty.LookupById(30102) }) if (property is not null) cache.Add(property);',
        '''AutomationProperty.LookupById(30102) })
        {
            if (property is not null)
            {
                cache.Add(property);
            }
        }''')

# Real relocated Broker E2E reproduced exit 3762504530 instead of rejection code 3 for JSON null.
rewrite('src/TypeLay.Broker/Program.cs',
        'request.Id <= last',
        'request.Id <= last || request.Token is null')
rewrite('tests/e2e/test_distribution.py', '    def test_packaged_worker_starts_in_isolated_mode_from_an_unrelated_directory(self):', '    def test_redistributed_frameworks_keep_their_exact_version_notices(self):\n        config = json.loads(\n            (self.home / "app/TypeLay.App.runtimeconfig.json").read_text(encoding="utf-8")\n        )\n        for framework in config["runtimeOptions"]["includedFrameworks"]:\n            package = (framework["name"] + ".Runtime.win-x64").lower()\n            folder = self.home / "app/licenses" / package / framework["version"]\n            with self.subTest(framework=framework["name"]):\n                self.assertTrue(folder.is_dir(), str(folder))\n                names = {path.name.upper(): path for path in folder.iterdir()}\n                licenses = [path for name, path in names.items() if name.startswith("LICENSE")]\n                notices = [path for name, path in names.items() if "NOTICE" in name]\n                self.assertTrue(licenses, "The upstream license must accompany the runtime")\n                self.assertTrue(notices, "The upstream third-party notices must be retained")\n                for path in licenses + notices:\n                    self.assertGreater(path.stat().st_size, 0, str(path))\n\n    def test_packaged_worker_starts_in_isolated_mode_from_an_unrelated_directory(self):')
rewrite('dev.ps1', '    # The deployment surface is explicit.', '    if ($SelfContained) {\n        # Ask the same SDK which packs were resolved, rather than guessing a NuGet cache or version.\n        $metadata = & dotnet msbuild \'src/TypeLay.App/TypeLay.App.csproj\' -nologo -verbosity:quiet `\n            -target:ResolveFrameworkReferences -getItem:ResolvedRuntimePack `\n            "-property:Configuration=$Configuration" -property:RuntimeIdentifier=win-x64 -property:SelfContained=true\n        if ($LASTEXITCODE -ne 0) { throw \'Could not resolve runtime-pack notices.\' }\n        $packs = @(($metadata -join "`n" | ConvertFrom-Json).Items.ResolvedRuntimePack)\n        if ($packs.Count -eq 0) { throw \'Self-contained publishing resolved no runtime packs.\' }\n        foreach ($pack in $packs) {\n            $notices = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File |\n                Where-Object { $_.Name -match \'^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]?PARTY)\' })\n            if (-not @($notices | Where-Object Name -Match \'^LICENSE\').Count -or\n                -not @($notices | Where-Object Name -Match \'NOTICE\').Count) {\n                throw "Runtime pack $($pack.NuGetPackageId) is missing upstream license or notices."\n            }\n            $destination = Join-Path $stage "app/licenses/$($pack.NuGetPackageId.ToLowerInvariant())/$($pack.NuGetPackageVersion)"\n            New-Item -ItemType Directory -Force $destination | Out-Null\n            foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }\n        }\n    }\n    # The deployment surface is explicit.')
shutil.copy2(root / 'THIRD_PARTY_NOTICES.md', root / 'docs/history/third-party-before-cleanup.md')
(root / 'THIRD_PARTY_NOTICES.md').write_text('# 第三方组件与分发边界\n\n## 发布包\n\n`./dev.ps1 pack` 生成 Windows x64 自包含运行包：TypeLay 启动器、App、Broker、Python worker 源文件、教程资源、启动脚本及 .NET Core / Windows Desktop 运行时。不分发 .NET SDK、测试、原型、开发工具、Matt skills、Python 解释器、模型权重或用户数据。\n\n运行时版本以 `app/TypeLay.App.runtimeconfig.json` 的 `includedFrameworks` 为准。发布过程从该次构建实际解析的 runtime packs 原样复制许可证与第三方 notices，按包名和版本保存到 `app/licenses/`。不得删改、翻译或用本文件替代上游声明；更新 SDK 或发布方式后重新检查包内文件并运行发行包测试。\n\n## 首次联网准备\n\n私有 Python、推理依赖及模型由 `src/TypeLay.App/Infrastructure/ModelInstaller.cs` 管理。Python worker 的直接依赖版本见 `src/TypeLay.Worker/requirements.txt`，不得在本文件另建一份易失配的版本清单。\n\n下载的 CPython、wheel、模型各自携带的许可证和 notices 必须保留。Argos 软件的许可证不能自动套用于模型权重或训练数据。引入离线模型包或新增二进制再分发前，逐项审查实际下载内容；本文件不是完整许可证审查结论。\n\n安装器将解析后的依赖来源与摘要写入用户数据目录中的 `dependency-lock.json`。模型索引未提供摘要时，首次下载得到的 SHA-256 仅记录当次内容，不等于上游预先认证的摘要。不要把版本约束或安装后记录描述成已附带所有依赖的离线供应链锁。\n\n## 仅用于开发\n\n`.agents/skills/` 保留 Matt Pocock 上游技能及参考文件，原始许可证位于 `.agents/MATT-LICENSE`；来源 revision 和安装约束见 `docs/agents/skills.md`，安装器记录位于 `skills-lock.json`。这些文件不进入 Windows 运行包。\n\n.NET SDK、xUnit、Ruff、Clang/LLD 用于构建、测试或格式检查，不作为工具链再分发。原生启动器只导入 Windows 系统 API。冻结的产品规格、原型及历史证据位于 `docs/reference/` 与 `docs/history/`，不作为发布包的一部分。\n\n旧源码交付包的声明保存在 `docs/history/third-party-before-cleanup.md`，不得用于判断当前运行包内容。\n', encoding='utf-8', newline='\n')
