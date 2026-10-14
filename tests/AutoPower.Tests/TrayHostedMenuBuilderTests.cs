using AutoPower.App;

namespace AutoPower.Tests;

[TestClass]
public sealed class TrayHostedMenuBuilderTests
{
    [TestMethod]
    public void BuildMirrorsTrayMenuLayoutOrderAndIds()
    {
        var items = TrayHostedMenuBuilder.Build(quickShutdownBusy: false, nextScheduleSummary: "내일 오전 7:00");

        // Protocol v1 has no submenus: the extra quick schedules follow, one separator per action group.
        Assert.HasCount(TrayMenuLayout.Entries.Count + 3, items);
        CollectionAssert.AreEqual(
            new string?[]
            {
                TrayMenuActionIds.QuickShutdownOneHour,
                TrayMenuActionIds.QuickShutdownTwoHours,
                null,
                "quick-shutdown-30m",
                "quick-shutdown-3h",
                null,
                "quick-hibernate-30m",
                "quick-hibernate-1h",
                "quick-hibernate-2h",
                "quick-hibernate-3h",
                null,
                "quick-sleep-30m",
                "quick-sleep-1h",
                "quick-sleep-2h",
                "quick-sleep-3h",
                null,
                TrayMenuActionIds.NextSchedule,
                null,
                TrayMenuActionIds.OpenApp,
                TrayMenuActionIds.NewSchedule,
                TrayMenuActionIds.PauseAll,
                TrayMenuActionIds.ResumeAll,
                null,
                TrayMenuActionIds.ExitApp,
            },
            items.Select(item => item.Id).ToArray());
        Assert.AreEqual(6, items.Count(item => item.IsSeparator));
    }

    [TestMethod]
    public void BuildEmbedsNextScheduleSummaryAsDisabledItem()
    {
        var items = TrayHostedMenuBuilder.Build(quickShutdownBusy: false, nextScheduleSummary: "8월 10일 오후 11:30");

        var nextSchedule = items.Single(item => item.Id == TrayMenuActionIds.NextSchedule);
        Assert.IsFalse(nextSchedule.Enabled);
        StringAssert.Contains(nextSchedule.Text, "8월 10일 오후 11:30");
    }

    [TestMethod]
    public void BuildDisablesQuickShutdownWhileBusy()
    {
        var busyItems = TrayHostedMenuBuilder.Build(quickShutdownBusy: true, nextScheduleSummary: "없음");
        var idleItems = TrayHostedMenuBuilder.Build(quickShutdownBusy: false, nextScheduleSummary: "없음");

        Assert.IsFalse(busyItems.Single(item => item.Id == TrayMenuActionIds.QuickShutdownOneHour).Enabled);
        Assert.IsFalse(busyItems.Single(item => item.Id == TrayMenuActionIds.QuickShutdownTwoHours).Enabled);
        Assert.IsTrue(idleItems.Single(item => item.Id == TrayMenuActionIds.QuickShutdownOneHour).Enabled);
        Assert.IsTrue(idleItems.Single(item => item.Id == TrayMenuActionIds.QuickShutdownTwoHours).Enabled);
        Assert.IsTrue(busyItems.Single(item => item.Id == TrayMenuActionIds.ExitApp).Enabled);
    }

    [TestMethod]
    public void EveryLayoutEntryExceptSeparatorsCarriesDistinctActionId()
    {
        var ids = TrayMenuLayout.Entries
            .Where(entry => entry.Command != TrayMenuCommand.Separator)
            .Select(entry => entry.ActionId)
            .ToArray();

        Assert.IsFalse(ids.Any(string.IsNullOrWhiteSpace));
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
