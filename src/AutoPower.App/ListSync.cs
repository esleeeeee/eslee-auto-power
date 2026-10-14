using System.Collections.ObjectModel;

namespace AutoPower.App;

/// <summary>Brings an observable collection to a target order with the fewest replacements.</summary>
internal static class ListSync
{
    public static void Apply<T, TKey>(
        ObservableCollection<T> current,
        IReadOnlyList<T> target,
        Func<T, TKey> keyOf,
        Func<T, T, bool> sameState)
        where TKey : notnull
    {
        for (var index = 0; index < target.Count; index++)
        {
            var wanted = target[index];
            var key = keyOf(wanted);
            var existing = -1;
            for (var search = index; search < current.Count; search++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keyOf(current[search]), key))
                {
                    existing = search;
                    break;
                }
            }

            if (existing < 0)
            {
                current.Insert(index, wanted);
                continue;
            }

            if (existing != index)
            {
                current.Move(existing, index);
            }

            if (!sameState(current[index], wanted))
            {
                current[index] = wanted;
            }
        }

        while (current.Count > target.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
    }
}
