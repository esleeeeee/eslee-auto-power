using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AutoPower.Windows;

public static class ResumeSignInPolicy
{
    // Winlogon must observe the temporary setting throughout resume. Restoring it at the
    // WakeToRun instant races the lock screen on slower systems.
    public static TimeSpan RestoreDelayAfterResume { get; } = TimeSpan.FromSeconds(20);
}

public enum ResumeSignInJournalState
{
    Preparing,
    Armed,
    Restoring,
    Restored,
    RestoreFailed
}

public sealed record ResumeSignInSnapshot(Guid SchemeGuid, uint AcValue, uint DcValue);

public sealed record ResumeSignInJournal(
    Guid OperationId,
    Guid ScheduleId,
    DateTime ScheduledLocalTime,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ResumeSignInJournalState State,
    ResumeSignInSnapshot PreviousSettings,
    string? LastError);

public interface IResumeSignInJournalStore
{
    ResumeSignInJournal? Read();
    void Write(ResumeSignInJournal journal);
    void Delete();
}

public sealed class JsonResumeSignInJournalStore : IResumeSignInJournalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonResumeSignInJournalStore(string? path = null)
    {
        _path = path ?? AppPaths.ResumeSignInJournalPath;
    }

    public ResumeSignInJournal? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ResumeSignInJournal>(File.ReadAllText(_path), Options)
               ?? throw new InvalidDataException("S3/S4 1회 자동 로그인 복구 정보가 손상되었습니다.");
    }

    public void Write(ResumeSignInJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        File.Move(temporary, _path, true);
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

public interface IResumeSignInSettings
{
    ResumeSignInSnapshot Capture();
    void SetRequireSignIn(ResumeSignInSnapshot snapshot, bool required);
    bool IsRequireSignInDisabled(ResumeSignInSnapshot snapshot);
    void Restore(ResumeSignInSnapshot snapshot);
    bool IsRestored(ResumeSignInSnapshot snapshot);
}

public sealed class ResumeSignInManager
{
    private const string MutexName = @"Global\eslee.AutoPower.ResumeSignIn.v1";
    private readonly IResumeSignInSettings _settings;
    private readonly IResumeSignInJournalStore _journalStore;
    private readonly TechnicalLogger _logger;

    public ResumeSignInManager(
        IResumeSignInSettings settings,
        IResumeSignInJournalStore journalStore,
        TechnicalLogger logger)
    {
        _settings = settings;
        _journalStore = journalStore;
        _logger = logger;
    }

    public ResumeSignInJournal? CurrentJournal => _journalStore.Read();

    public ResumeSignInJournal Arm(Guid scheduleId, DateTime scheduledLocalTime)
    {
        using var lease = AcquireLease();
        var active = _journalStore.Read();
        if (active is not null && active.State is not ResumeSignInJournalState.Restored)
        {
            throw new InvalidOperationException("복구되지 않은 S3/S4 1회 자동 로그인 상태가 있어 새 예약을 준비할 수 없습니다.");
        }

        if (active is not null)
        {
            _journalStore.Delete();
        }

        var now = DateTimeOffset.UtcNow;
        var journal = new ResumeSignInJournal(
            Guid.NewGuid(),
            scheduleId,
            DateTime.SpecifyKind(scheduledLocalTime, DateTimeKind.Unspecified),
            now,
            now,
            ResumeSignInJournalState.Preparing,
            _settings.Capture(),
            null);
        _journalStore.Write(journal);

        try
        {
            _settings.SetRequireSignIn(journal.PreviousSettings, required: false);
            if (!_settings.IsRequireSignInDisabled(journal.PreviousSettings))
            {
                throw new InvalidOperationException("절전 모드 해제 시 로그인 요구를 임시 해제한 뒤 검증하지 못했습니다.");
            }

            journal = journal with
            {
                State = ResumeSignInJournalState.Armed,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            _journalStore.Write(journal);
            _logger.Information("resume-signin.armed", $"schedule={scheduleId:D}");
            return journal;
        }
        catch (Exception error)
        {
            try
            {
                _settings.Restore(journal.PreviousSettings);
                if (!_settings.IsRestored(journal.PreviousSettings))
                {
                    throw new InvalidOperationException("1회 자동 로그인 준비 실패 후 원래 로그인 요구 설정을 복원하지 못했습니다.");
                }

                _journalStore.Delete();
            }
            catch (Exception rollbackError)
            {
                journal = journal with
                {
                    State = ResumeSignInJournalState.RestoreFailed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    LastError = rollbackError.GetType().Name
                };
                _journalStore.Write(journal);
                _logger.Error("resume-signin.arm.rollback-failed", rollbackError, $"schedule={scheduleId:D}");
            }

            _logger.Error("resume-signin.arm.failed", error, $"schedule={scheduleId:D}");
            throw;
        }
    }

    public ResumeSignInJournal? Restore(Guid? expectedScheduleId = null)
    {
        using var lease = AcquireLease();
        var journal = _journalStore.Read();
        if (journal is null || journal.State == ResumeSignInJournalState.Restored)
        {
            return null;
        }

        if (expectedScheduleId is Guid scheduleId && journal.ScheduleId != scheduleId)
        {
            return null;
        }

        journal = journal with
        {
            State = ResumeSignInJournalState.Restoring,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastError = null
        };
        _journalStore.Write(journal);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _settings.Restore(journal.PreviousSettings);
                if (!_settings.IsRestored(journal.PreviousSettings))
                {
                    throw new InvalidOperationException("원래 절전 해제 로그인 요구 설정의 복원 검증에 실패했습니다.");
                }

                journal = journal with
                {
                    State = ResumeSignInJournalState.Restored,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    LastError = null
                };
                _journalStore.Write(journal);
                _logger.Information("resume-signin.restored", $"schedule={journal.ScheduleId:D}");
                return journal;
            }
            catch (Exception error)
            {
                lastError = error;
                _logger.Error("resume-signin.restore.retry", error,
                    $"schedule={journal.ScheduleId:D};attempt={attempt}");
            }
        }

        journal = journal with
        {
            State = ResumeSignInJournalState.RestoreFailed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastError = lastError?.GetType().Name
        };
        _journalStore.Write(journal);
        throw new InvalidOperationException(
            "S3/S4 1회 자동 로그인 후 원래 로그인 요구 설정을 복원하지 못했습니다. 관리자 권한 복구가 필요합니다.",
            lastError);
    }

    public void DeleteCompletedJournal()
    {
        using var lease = AcquireLease();
        if (_journalStore.Read()?.State == ResumeSignInJournalState.Restored)
        {
            _journalStore.Delete();
        }
    }

    private static MutexLease AcquireLease()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("S3/S4 1회 자동 로그인 상태를 다른 프로세스가 처리 중입니다.");
            }

            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}

public sealed class WindowsResumeSignInSettings : IResumeSignInSettings
{
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid ConsoleLock = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");

    public ResumeSignInSnapshot Capture()
    {
        ThrowIfFailed(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "현재 전원 구성표를 읽지 못했습니다.");
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            return Read(scheme);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                LocalFree(pointer);
            }
        }
    }

    public void SetRequireSignIn(ResumeSignInSnapshot snapshot, bool required)
    {
        var value = required ? 1u : 0u;
        Write(snapshot.SchemeGuid, value, value);
    }

    public bool IsRequireSignInDisabled(ResumeSignInSnapshot snapshot)
    {
        var current = Read(snapshot.SchemeGuid);
        return current.AcValue == 0 && current.DcValue == 0;
    }

    public void Restore(ResumeSignInSnapshot snapshot) =>
        Write(snapshot.SchemeGuid, snapshot.AcValue, snapshot.DcValue);

    public bool IsRestored(ResumeSignInSnapshot snapshot)
    {
        var current = Read(snapshot.SchemeGuid);
        return current.AcValue == snapshot.AcValue && current.DcValue == snapshot.DcValue;
    }

    private static ResumeSignInSnapshot Read(Guid scheme)
    {
        var subgroup = NoSubgroup;
        var setting = ConsoleLock;
        ThrowIfFailed(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var ac),
            "AC 전원의 절전 해제 로그인 요구 값을 읽지 못했습니다.");
        ThrowIfFailed(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dc),
            "DC 전원의 절전 해제 로그인 요구 값을 읽지 못했습니다.");
        return new ResumeSignInSnapshot(scheme, ac, dc);
    }

    private static void Write(Guid scheme, uint ac, uint dc)
    {
        var subgroup = NoSubgroup;
        var setting = ConsoleLock;
        ThrowIfFailed(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ac),
            "AC 전원의 절전 해제 로그인 요구 값을 변경하지 못했습니다.");
        ThrowIfFailed(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dc),
            "DC 전원의 절전 해제 로그인 요구 값을 변경하지 못했습니다.");
        ThrowIfFailed(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "변경한 전원 구성표를 적용하지 못했습니다.");
    }

    private static void ThrowIfFailed(uint result, string message)
    {
        if (result != 0)
        {
            throw new Win32Exception(checked((int)result), message);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid powerSettingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid,
        ref Guid powerSettingGuid, uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
