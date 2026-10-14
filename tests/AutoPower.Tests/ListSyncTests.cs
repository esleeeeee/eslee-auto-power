using System.Collections.ObjectModel;
using AutoPower.App;

namespace AutoPower.Tests;

[TestClass]
public sealed class ListSyncTests
{
    private static readonly int[] ExpectedOrder = [3, 4, 1];

    private sealed record Item(int Id, string State);

    [TestMethod]
    public void UnchangedItemsKeepTheirInstances()
    {
        var a = new Item(1, "on");
        var b = new Item(2, "on");
        var list = new ObservableCollection<Item> { a, b };

        ListSync.Apply(list, [new Item(1, "on"), new Item(2, "off")], item => item.Id, (x, y) => x.State == y.State);

        Assert.AreSame(a, list[0]);
        Assert.AreNotSame(b, list[1]);
        Assert.AreEqual("off", list[1].State);
    }

    [TestMethod]
    public void InsertsMovesAndRemovesToMatchTheTargetOrder()
    {
        var one = new Item(1, "x");
        var three = new Item(3, "x");
        var list = new ObservableCollection<Item> { one, new Item(2, "x"), three };

        ListSync.Apply(list, [new Item(3, "x"), new Item(4, "x"), new Item(1, "x")], item => item.Id, (x, y) => x.State == y.State);

        CollectionAssert.AreEqual(ExpectedOrder, list.Select(item => item.Id).ToArray());
        Assert.AreSame(three, list[0]);
        Assert.AreSame(one, list[2]);
    }
}
