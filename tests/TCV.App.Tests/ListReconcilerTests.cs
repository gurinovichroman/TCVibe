using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FluentAssertions;
using TCV.App.Collections;

namespace TCV.App.Tests;

/// <summary>
/// The folder trees and the drive selector merge a fresh list into the bound one. What must hold:
/// the result equals the target, and an item present in both is the same object afterwards — that
/// is what keeps an open subtree open and the selected drive selected.
/// </summary>
public class ListReconcilerTests
{
    private sealed record Item(string Name);

    [Fact]
    public void Reconcile_RemovesInsertsAndMoves_ToMatchTheTarget()
    {
        Item a = new("a"), b = new("b"), c = new("c"), d = new("d");
        var current = new ObservableCollection<Item> { a, b, c };

        var changed = ListReconciler.Reconcile(current, [c, a, d]);

        changed.Should().BeTrue();
        current.Should().Equal(c, a, d);
    }

    [Fact]
    public void Reconcile_KeepsTheObjectsPresentInBoth()
    {
        Item a = new("a"), b = new("b");
        var current = new ObservableCollection<Item> { a, b };

        ListReconciler.Reconcile(current, [new Item("x"), b, a]);

        current[1].Should().BeSameAs(b);
        current[2].Should().BeSameAs(a);
    }

    [Fact]
    public void Reconcile_SameList_ChangesNothingAndRaisesNothing()
    {
        Item a = new("a"), b = new("b");
        var current = new ObservableCollection<Item> { a, b };
        var events = 0;
        current.CollectionChanged += (_, _) => events++;

        var changed = ListReconciler.Reconcile(current, [a, b]);

        changed.Should().BeFalse();
        events.Should().Be(0);
    }

    [Fact]
    public void Reconcile_AReorder_IsAMoveNotARemoveAndAdd()
    {
        // A remove-and-add would drop the item's container — and with it a tree node's open subtree.
        Item a = new("a"), b = new("b"), c = new("c");
        var current = new ObservableCollection<Item> { a, b, c };
        var actions = new List<NotifyCollectionChangedAction>();
        current.CollectionChanged += (_, e) => actions.Add(e.Action);

        ListReconciler.Reconcile(current, [c, a, b]);

        current.Should().Equal(c, a, b);
        actions.Should().OnlyContain(action => action == NotifyCollectionChangedAction.Move);
    }

    [Fact]
    public void Reconcile_EmptyTarget_EmptiesTheList()
    {
        var current = new List<Item> { new("a"), new("b") };

        ListReconciler.Reconcile(current, []);

        current.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_PlainList_IsReorderedToo()
    {
        Item a = new("a"), b = new("b"), c = new("c");
        var current = new List<Item> { a, b, c };

        ListReconciler.Reconcile(current, [b, c, a]);

        current.Should().Equal(b, c, a);
    }
}
