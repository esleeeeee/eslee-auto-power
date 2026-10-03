namespace AutoPower.Core;

public static class ShutdownConsequence
{
    public static string Describe(PowerSchedule? nextWake) => nextWake is null
        ? AppText.IsEnglish
            ? "After a full shutdown, this app cannot turn the PC back on for a scheduled wake. To use scheduled wake later, leave the PC in sleep or hibernation instead."
            : "완전히 종료된 PC는 앱의 자동 시작 예약으로 다시 켤 수 없습니다. 이후 자동 시작을 사용하려면 PC를 절전 또는 최대 절전 상태로 두어야 합니다."
        : AppText.IsEnglish
            ? $"A scheduled wake exists at {nextWake.ScheduledLocalDateTime:MMM dd HH:mm}. A full shutdown prevents that wake from turning the PC back on. Hibernation is recommended for a long wait, and sleep for a short wait."
            : $"{nextWake.ScheduledLocalDateTime:MM월 dd일 HH:mm}에 다음 자동 시작 예약이 있습니다. PC를 완전히 종료하면 해당 시각에 자동으로 다시 시작할 수 없습니다. 장시간 대기는 최대 절전, 짧은 대기는 절전을 권장합니다.";
}
