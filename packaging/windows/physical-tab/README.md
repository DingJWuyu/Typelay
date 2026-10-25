# 隔离物理 Tab 验证包

本包不是 TypeLay 产品体验包，不启动正式 App/Main，也不开放正式自动接受。它直接运行现有 xUnit 3.2.2 的一条人工用例，只在自己创建的原生输入框使用合成原文“你好”和固定候选“Hello”，不读取你的文档、不调用翻译模型。

## 准备

- Windows x64、已解锁且由你独占的桌面和真实键盘。先保存正在编辑的内容；测试期间不要切窗口、打字或使用远程/宏/软件注键。
- 预先安装 **.NET 10 Desktop Runtime x64**，且同一 x64 安装的 `dotnet` 在 PATH。需要 Desktop Runtime，不是仅 ASP.NET/Core Runtime；不需要 SDK、Python、模型或管理员权限。本包不安装或修改系统组件。
- 完整解压 `TypeLay-Windows-physical-Tab-validation.zip`，保留 `TypeLay.slnx` 和所有子目录原布局。`BUILD.txt` 是源码及构建身份；外部 `.sha256` 对应整个 ZIP。
- 在**非管理员、64位 Windows PowerShell**中进入解压后的 `TypeLay-Physical-Tab` 目录。不要双击测试 exe，也不要运行整套测试或 F24。

## 只运行一次 Tab

下面整个代码块复制一次执行。它先验证唯一、未跳过的 Tab 行，再用完全相同的过滤条件运行，不使用随路径变化的 case ID。出现原生源框且控制台提示按键后，在该框中**亲手按下并释放一次无修饰 Tab**；不按住、不补按、不模拟、不重跑。原 fixture 总操作窗仍为30秒，每份 grant 仍为100ms；脚本不延长或续期许可。

```powershell
& {
    $ErrorActionPreference = 'Stop'
    $originalEncoding = [Console]::OutputEncoding
    try {
        # xUnit emits UTF-8; decode native output before PS5 writes or parses it.
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        if (-not [Environment]::Is64BitProcess) { throw '请使用64位PowerShell。' }
        $runtime = @(& dotnet --list-runtimes)
        if ($LASTEXITCODE -ne 0 -or -not ($runtime -match '^Microsoft.WindowsDesktop.App 10\.')) {
            throw '需要PATH中的.NET 10 Desktop Runtime x64；不要安装SDK或更改权限来绕过。'
        }
        $root = (Get-Location).Path
        $exe = Join-Path $root 'tests/windows/TypeLay.App.Tests/bin/Release/net10.0-windows/TypeLay.App.Tests.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $root 'TypeLay.slnx') -PathType Leaf)) {
            throw '请进入完整解压后的TypeLay-Physical-Tab目录。'
        }
        $result = Join-Path $root ('results/' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $result | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'BUILD.txt') -Destination $result
        $class = 'TypeLay.App.Tests.NativeSourceControllerTests'
        $method = 'ManualPhysicalAcceptanceKeyUsesOrderedSourceOnceAndSettlesItsOriginalTails'
        $filter = @('-preEnumerateTheories', '-explicit', 'only', '-method', "$class.$method",
            '-trait', 'ManualPhysicalKey=Tab', '-noAutoReporters', '-noColor', '-noLogo')
        # PS5把native stderr包装为ErrorRecord；保留它，但不让普通stderr截断退出/结果检查。
        $ErrorActionPreference = 'Continue'
        & $exe @filter -list full/json 2> (Join-Path $result 'discovery.stderr') |
            Out-File -LiteralPath (Join-Path $result 'discovery.json') -Encoding utf8
        $discoveryExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $discoveryExit | Set-Content -LiteralPath (Join-Path $result 'discovery.exit-code.txt')
        if ($discoveryExit -ne 0) { throw "发现失败；保留$result，不运行或重试。" }
        $rows = @(Get-Content -LiteralPath (Join-Path $result 'discovery.json') -Raw | ConvertFrom-Json)
        if ($rows.Count -ne 1) { throw '没有唯一Tab行；停止，不修改过滤条件。' }
        $row = $rows[0]
        if ([IO.Path]::GetFullPath($row.Assembly) -ne [IO.Path]::ChangeExtension($exe, '.dll') -or
            $row.Class -ne $class -or $row.Method -ne $method -or
            $row.PSObject.Properties.Name -contains 'Skip' -or
            @($row.Traits.ManualPhysicalKey).Count -ne 1 -or $row.Traits.ManualPhysicalKey[0] -ne 'Tab') {
            throw '发现元数据不匹配；停止，不运行其它键。'
        }
        $trx = Join-Path $result 'physical-tab.trx'
        Write-Host '等待原生框及按键提示后，只亲手按下并释放一次Tab。不要切窗或补按。'
        $ErrorActionPreference = 'Continue'
        & $exe @filter -trx $trx 2> (Join-Path $result 'runner.stderr') |
            Tee-Object -FilePath (Join-Path $result 'runner.log')
        $runExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        $runExit | Set-Content -LiteralPath (Join-Path $result 'runner.exit-code.txt')
        if ($runExit -ne 0 -or -not (Test-Path -LiteralPath $trx -PathType Leaf)) {
            throw "执行失败或结果缺失；保留$result，不补按或重跑。"
        }
        [xml]$report = Get-Content -LiteralPath $trx -Raw -Encoding UTF8
        $counter = $report.TestRun.ResultSummary.Counters
        $definitions = @($report.TestRun.TestDefinitions.UnitTest)
        $tests = @($report.TestRun.Results.UnitTestResult)
        if ($report.TestRun.ResultSummary.outcome -ne 'Completed' -or
            $counter.total -ne '1' -or $counter.executed -ne '1' -or $counter.passed -ne '1' -or
            $counter.failed -ne '0' -or $counter.error -ne '0' -or
            $counter.notExecuted -ne '0' -or $counter.notRunnable -ne '0' -or
            $tests.Count -ne 1 -or $tests[0].outcome -ne 'Passed' -or $definitions.Count -ne 1 -or
            $definitions[0].TestMethod.className -ne $class -or $definitions[0].TestMethod.name -ne $method -or
            $definitions[0].TestMethod.codeBase -ne [IO.Path]::ChangeExtension($exe, '.dll') -or
            $tests[0].testId -ne $definitions[0].id) {
            throw "不是恰好1 executed/1 Passed/零跳过；保留$result，不算通过。"
        }
        Write-Host "本次隔离物理Tab用例通过。请保留原始结果：$result"
    } finally {
        [Console]::OutputEncoding = $originalEncoding
    }
}
```

测试成功须断言一次 Verified、原按键零派发、零 LL Activate/二次写、原尾键退休，随后自动 Undo 回“你好”。临时设置删除、Hook释放和精确自有源/Broker进程退出沿现有 owner；清理含有界终止，不称自然退出。失败、超时、退出未知或零测试均不是通过，保留结果后停止，不靠再次按键补救。请反馈 ZIP SHA、BUILD.txt、discovery JSON、TRX、stdout/stderr及退出码；其中路径可能包含本机用户名，分享前检查隐私。

CI先要求原App套件中的 Tab/F7 **普通注入拒绝**两行完整通过。搬迁到checkout外的新含空格/中文目录后，仅运行既有factory默认关闭用例的 automation false/true 两行，要求恰好2 executed/2 Passed、零跳过及runner错误，并发现唯一人工Tab行，才输出本ZIP。搬迁smoke用真实WPF、源fixture和显式注入的自有Broker验证加载、默认cap0、不安装/不提示接受及零写；不是正式BrokerClient启动、source-install或注入拒绝的再次验收。

构建身份与复制链来自同一source、同一job的Release输出，三处NativeInput须一致；没有独立的原套件测试时快照，不声称原套件与最终包逐字samebits。CI不运行物理阳性；本机一次物理阳性也不等于 UIAccess、真实 IME composition、第三方部署或正式 Main/capability0 接受通过。
