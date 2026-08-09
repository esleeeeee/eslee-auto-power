using System.IO.Pipes;
using System.Text;
using AutoPower.App;

namespace AutoPower.Tests;

[TestClass]
public sealed class TrayHostLinkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ExpectedMenuActionSequence = ["pause-all", "no-such-action"];

    [TestMethod]
    public void BuildPipeNameMatchesTrayFolderConvention()
    {
        // Tray Folder 저장소의 TrayPipeProtocolTests와 같은 기대값을 사용해
        // 저장소 간 파이프 이름 규약이 일치하는지 확인합니다.
        Assert.AreEqual(
            "eslee.trayfolder.tray-host.v1.user-1_a--",
            TrayHostLink.BuildPipeName("user 1_a!한"));
    }

    [TestMethod]
    public async Task RegistersAppliesHostedModeAndAnswersCommands()
    {
        var pipeName = CreatePipeName();
        var hiddenSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var link = CreateLink(
            pipeName,
            visible =>
            {
                if (!visible)
                {
                    hiddenSignal.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            () =>
            {
                activated.TrySetResult(true);
                return Task.CompletedTask;
            });
        link.Start();

        var server = CreateServer(pipeName);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = CreateReader(server);
            var writer = CreateWriter(server);
            await using (writer.ConfigureAwait(false))
            {
                var registerLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(registerLine);
                StringAssert.Contains(registerLine, "\"type\":\"register\"");
                StringAssert.Contains(registerLine, "\"protocolVersion\":1");
                StringAssert.Contains(registerLine, "\"appId\":\"eslee.autopower\"");
                StringAssert.Contains(registerLine, "\"processId\":4321");

                await writer.WriteLineAsync("""{"type":"set-tray-mode","mode":"hosted"}""");
                Assert.IsTrue(await hiddenSignal.Task.WaitAsync(TestTimeout));

                await writer.WriteLineAsync("""{"type":"command","id":7,"command":"activate"}""");
                Assert.IsTrue(await activated.Task.WaitAsync(TestTimeout));
                var activateResult = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(activateResult);
                StringAssert.Contains(activateResult, "\"type\":\"command-result\"");
                StringAssert.Contains(activateResult, "\"id\":7");
                StringAssert.Contains(activateResult, "\"succeeded\":true");

                await writer.WriteLineAsync("""{"type":"command","id":9,"command":"shutdown"}""");
                var unsupportedResult = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(unsupportedResult);
                StringAssert.Contains(unsupportedResult, "\"id\":9");
                StringAssert.Contains(unsupportedResult, "\"succeeded\":false");
            }
        }
    }

    [TestMethod]
    public async Task GetMenuRepliesWithProvidedItems()
    {
        var pipeName = CreatePipeName();
        using var link = CreateLink(
            pipeName,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            getMenuItemsAsync: () => Task.FromResult<IReadOnlyList<TrayHostMenuItem>>(
            [
                TrayHostMenuItem.Action("open-app", "앱 열기"),
                TrayHostMenuItem.Separator,
                TrayHostMenuItem.Action("next-schedule", "다음 예약: 내일 오전 7:00", enabled: false),
            ]));
        link.Start();

        var server = CreateServer(pipeName);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = CreateReader(server);
            var writer = CreateWriter(server);
            await using (writer.ConfigureAwait(false))
            {
                Assert.IsNotNull(await reader.ReadLineAsync().WaitAsync(TestTimeout));

                await writer.WriteLineAsync("""{"type":"get-menu","id":11}""");
                var menuLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(menuLine);
                StringAssert.Contains(menuLine, "\"type\":\"menu\"");
                StringAssert.Contains(menuLine, "\"id\":11");
                StringAssert.Contains(menuLine, "\"id\":\"open-app\"");
                StringAssert.Contains(menuLine, "\"separator\":true");
                StringAssert.Contains(menuLine, "\"enabled\":false");
                Assert.IsFalse(menuLine.Contains("\"enabled\":true", StringComparison.Ordinal));
            }
        }
    }

    [TestMethod]
    public async Task MenuActionCommandInvokesCallbackAndReportsUnknownIds()
    {
        var pipeName = CreatePipeName();
        var executed = new List<string>();
        using var link = CreateLink(
            pipeName,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            executeMenuActionAsync: actionId =>
            {
                lock (executed)
                {
                    executed.Add(actionId);
                }

                return Task.FromResult(actionId == "pause-all");
            });
        link.Start();

        var server = CreateServer(pipeName);
        await using (server.ConfigureAwait(false))
        {
            await server.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = CreateReader(server);
            var writer = CreateWriter(server);
            await using (writer.ConfigureAwait(false))
            {
                Assert.IsNotNull(await reader.ReadLineAsync().WaitAsync(TestTimeout));

                await writer.WriteLineAsync(
                    """{"type":"command","id":21,"command":"menu-action","actionId":"pause-all"}""");
                var knownResult = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(knownResult);
                StringAssert.Contains(knownResult, "\"id\":21");
                StringAssert.Contains(knownResult, "\"succeeded\":true");

                await writer.WriteLineAsync(
                    """{"type":"command","id":22,"command":"menu-action","actionId":"no-such-action"}""");
                var unknownResult = await reader.ReadLineAsync().WaitAsync(TestTimeout);
                Assert.IsNotNull(unknownResult);
                StringAssert.Contains(unknownResult, "\"id\":22");
                StringAssert.Contains(unknownResult, "\"succeeded\":false");

                lock (executed)
                {
                    CollectionAssert.AreEqual(ExpectedMenuActionSequence, executed);
                }
            }
        }
    }

    [TestMethod]
    public async Task DisconnectRestoresStandaloneAndReconnectsToNewHost()
    {
        var pipeName = CreatePipeName();
        var hiddenSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visibleSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var link = CreateLink(
            pipeName,
            visible =>
            {
                if (visible)
                {
                    visibleSignal.TrySetResult(true);
                }
                else
                {
                    hiddenSignal.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            () => Task.CompletedTask);
        link.Start();

        var firstServer = CreateServer(pipeName);
        await using (firstServer.ConfigureAwait(false))
        {
            await firstServer.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = CreateReader(firstServer);
            var writer = CreateWriter(firstServer);
            await using (writer.ConfigureAwait(false))
            {
                Assert.IsNotNull(await reader.ReadLineAsync().WaitAsync(TestTimeout));
                await writer.WriteLineAsync("""{"type":"set-tray-mode","mode":"hosted"}""");
                Assert.IsTrue(await hiddenSignal.Task.WaitAsync(TestTimeout));
            }
        }

        // 호스트가 종료되면 클라이언트는 자체 트레이 아이콘을 다시 표시해야 합니다.
        Assert.IsTrue(await visibleSignal.Task.WaitAsync(TestTimeout));

        // 호스트가 다시 실행되면(Tray Folder 재실행) 클라이언트가 재연결해 다시 등록해야 합니다.
        var secondServer = CreateServer(pipeName);
        await using (secondServer.ConfigureAwait(false))
        {
            await secondServer.WaitForConnectionAsync().WaitAsync(TestTimeout);
            using var reader = CreateReader(secondServer);
            var registerLine = await reader.ReadLineAsync().WaitAsync(TestTimeout);
            Assert.IsNotNull(registerLine);
            StringAssert.Contains(registerLine, "\"type\":\"register\"");
        }
    }

    private static string CreatePipeName() =>
        "eslee.trayfolder.link-test." + Guid.NewGuid().ToString("N");

    private static TrayHostLink CreateLink(
        string pipeName,
        Func<bool, Task> applyTrayIconVisibleAsync,
        Func<Task> activateAsync,
        Func<Task<IReadOnlyList<TrayHostMenuItem>>>? getMenuItemsAsync = null,
        Func<string, Task<bool>>? executeMenuActionAsync = null) => new(
        pipeName,
        processId: 4321,
        applyTrayIconVisibleAsync,
        activateAsync,
        getMenuItemsAsync ?? (() => Task.FromResult<IReadOnlyList<TrayHostMenuItem>>([])),
        executeMenuActionAsync ?? (_ => Task.FromResult(false)),
        (_, _) => { },
        (_, _) => { },
        reconnectDelay: TimeSpan.FromMilliseconds(100),
        connectTimeout: TimeSpan.FromMilliseconds(500));

    // 버퍼 크기를 지정하지 않으면 0 할당량 파이프가 되어, 상대편이 읽기를 걸어두기
    // 전까지 쓰기가 완료되지 않습니다. 실제 호스트(TrayHostServer)와 같은 값을 씁니다.
    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous,
        inBufferSize: 4096,
        outBufferSize: 4096);

    private static StreamReader CreateReader(Stream stream) => new(
        stream,
        Encoding.UTF8,
        detectEncodingFromByteOrderMarks: false,
        bufferSize: 1024,
        leaveOpen: true);

    private static StreamWriter CreateWriter(Stream stream) => new(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 1024,
        leaveOpen: true)
    {
        AutoFlush = true,
    };
}
