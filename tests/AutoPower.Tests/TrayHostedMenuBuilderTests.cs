using AutoPower.App;

namespace AutoPower.Tests;

[TestClass]
public sealed class TrayHostedMenuBuilderTests
{
    [TestMethod]
    public void BuildMirrorsTrayMenuLayoutOrderAndIds()
    {
        var items = TrayHostedMenuBuilder.Build(quickShutdownBusy: false, nextScheduleSummary: "내일 오전 7:00");

        Assert.HasCount(TrayMenuLayout.Entries.Count, items);
        CollectionAssert.AreEqual(
            new string?[]
            {
                TrayMenuActionIds.QuickShutdownOneHour,
                TrayMenuActionIds.QuickShutdownTwoHours,
                null,
                TrayMenuActionIds.NextSchedule,
                null,
                TrayMenuActionIds.OpenApp,
                TrayMenuActionIds.NewSchedule,
                TrayMenuActionIds.PauseAll,
                null,
                TrayMenuActionIds.ExitApp,
            },
            items.Select(item => item.Id).ToArray());
        Assert.AreEqual(3, items.Count(item => item.IsSeparator));
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
