using System.Globalization;
using AutoPower.Core;

namespace AutoPower.App;

/// <summary>Display texts shared by the main window, tray and dialogs.</summary>
internal static class UiText
{
    public static string ActionName(PowerActionType action) => AppText.T(action switch
    {
        PowerActionType.WakeFromSleep => "자동 시작 (S3)",
        PowerActionType.WakeFromHibernate => "자동 시작 (S4)",
        PowerActionType.Shutdown => "완전 종료",
        PowerActionType.Hibernate => "최대 절전",
        PowerActionType.Sleep => "절전",
        _ => "이전 전원 동작"
    });

    /// <summary>"오늘 23:00", "내일 07:30", "10.18 (토) 23:00".</summary>
    public static string When(DateTime scheduledLocal, DateTime nowLocal)
    {
        var time = scheduledLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (scheduledLocal.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => AppText.F("오늘 {0}", time),
            1 => AppText.F("내일 {0}", time),
            _ => $"{DateLabel(scheduledLocal)} {time}"
        };
    }

    public static string DateLabel(DateTime value) =>
        value.ToString(AppText.IsEnglish ? "ddd, MMM d" : "M.d (ddd)", AppText.Culture);

    public static string LongDate(DateTime value) =>
        value.ToString(AppText.IsEnglish ? "dddd, MMMM d, yyyy" : "yyyy년 M월 d일 dddd", AppText.Culture);

    /// <summary>Remaining time until <paramref name="target"/>, rounded up to the minute.</summary>
    public static string Remaining(DateTime target, DateTime now)
    {
        var span = target - now;
        if (span <= TimeSpan.Zero)
        {
            return AppText.T("곧 실행");
        }

        var totalMinutes = (int)Math.Ceiling(span.TotalMinutes);
        if (totalMinutes < 1)
        {
            return AppText.T("1분 미만 남음");
        }

        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            return hours > 0
                ? AppText.F("{0}일 {1}시간 남음", days, hours)
                : AppText.F("{0}일 남음", days);
        }

        if (hours > 0)
        {
            return minutes > 0
                ? AppText.F("{0}시간 {1}분 남음", hours, minutes)
                : AppText.F("{0}시간 남음", hours);
        }

        return AppText.F("{0}분 남음", minutes);
    }

    public static string ResultName(ResultKind result) => AppText.T(result switch
    {
        ResultKind.Success => "성공",
        ResultKind.Failure => "실패",
        ResultKind.Skipped => "건너뜀",
        ResultKind.Warning => "경고",
        _ => "정보"
    });

    /// <summary>Readable label for a history event type; unknown types are shown as stored.</summary>
    public static string EventName(string eventType) => eventType switch
    {
        "ScheduleCreated" => AppText.T("예약 생성"),
        "ScheduleUpdated" => AppText.T("예약 수정"),
        "ScheduleDeleted" => AppText.T("예약 삭제"),
        "ScheduleSkipped" => AppText.T("예약 건너뜀"),
        "ScheduleMissed" => AppText.T("예약 놓침"),
        "Snoozed" => AppText.T("예약 미룸"),
        "ScheduleRestored" => AppText.T("건너뛴 예약 다시 켬"),
        "ScheduleResumeConflict" => AppText.T("다시 켜기 충돌"),
        "PauseAll" or "SchedulesPaused" => AppText.T("모두 일시 중지"),
        "ResumeAll" or "SchedulesResumed" => AppText.T("모두 다시 켜기"),
        "RecurrenceAdvanced" => AppText.T("다음 반복 준비"),
        "RecurrenceAdvanceFailed" => AppText.T("다음 반복 준비 실패"),
        "ExecutionStarted" or "PowerAction" => AppText.T("전원 동작 시작"),
        "PowerTransitionCompleted" => AppText.T("전원 동작 완료"),
        "PowerTransitionFailed" => AppText.T("전원 동작 실패"),
        "PowerTransitionResultUnknown" => AppText.T("결과 확인 불가"),
        "PowerTransitionRecovered" => AppText.T("전원 동작 결과 확인"),
        "ShutdownFallbackInvoked" or "FallbackInvoked" => AppText.T("강제 종료 실행"),
        "ResumeSignInRestored" => AppText.T("로그인 요구 복원"),
        "RemovedPowerFeatureMigration" => AppText.T("이전 전원 예약 정리"),
        "S3WakeTest" => AppText.T("S3 깨우기 테스트"),
        "S4WakeTest" => AppText.T("S4 깨우기 테스트"),
        "ShutdownWakeWarning" => AppText.T("종료 후 자동 시작 경고"),
        "ShutdownPrimaryAccepted" or "ShutdownFallbackAccepted" => AppText.T("종료 요청 접수"),
        "ShutdownPrimaryRejected" => AppText.T("종료 요청 거부"),
        "ShutdownFallbackPending" or "FallbackPending" => AppText.T("강제 종료 대기"),
        "WakePrepared" => AppText.T("깨우기 준비"),
        "WakeTask" => AppText.T("깨우기 작업"),
        "WakeResumed" => AppText.T("깨어남"),
        "ResumeReady" => AppText.T("바탕화면 준비"),
        "ResumeSignInArmed" => AppText.T("1회 로그인 준비"),
        "FollowUpProgram" or "FollowUpPrograms" => AppText.T("후속 프로그램"),
        "ManualLowPower" => AppText.T("수동 전원 전환"),
        "ManualLowPowerResume" => AppText.T("수동 전환 후 복귀"),
        "CredentialSaved" => AppText.T("로그인 정보 저장"),
        "CredentialTest" => AppText.T("로그인 테스트"),
        "CredentialRemoved" => AppText.T("로그인 정보 제거"),
        "RemovedSecurityCleanup" => AppText.T("이전 보안 설정 정리"),
        _ => eventType
    };
}
