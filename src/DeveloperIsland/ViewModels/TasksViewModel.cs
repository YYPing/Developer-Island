using System.Collections.ObjectModel;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Tasks;

namespace DeveloperIsland.ViewModels;

public sealed class TaskRowViewModel : ObservableObject
{
    private TaskItem _item;

    public TaskRowViewModel(TaskItem item)
    {
        _item = item;
    }

    public Guid Id => _item.Id;

    public string Title => _item.Title;

    public bool Done => _item.Done;

    public double TitleOpacity => _item.Done ? 0.55 : 1.0;

    /// <summary>
    /// A done task gets a strikethrough. Opacity alone reads as "disabled" rather than "finished",
    /// and on a dark capsule the difference at 13 px is easy to miss.
    /// </summary>
    public Windows.UI.Text.TextDecorations TitleDecorations =>
        _item.Done ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

    public double CheckOpacity => _item.Done ? 1.0 : 0.0;

    public string CheckGlyph => _item.Done ? "" : string.Empty;

    public string AccessibleName => _item.Done ? $"{_item.Title}, done" : _item.Title;

    public void Set(TaskItem item)
    {
        if (item == _item)
        {
            return;
        }

        _item = item;
        OnAllPropertiesChanged();
    }
}

/// <summary>Local to-do list with quick capture. Rows are reused so the list does not flicker.</summary>
public sealed class TasksViewModel : ModuleViewModel
{
    private TaskStore? _store;

    public TasksViewModel()
        : base(ModuleId.Tasks)
    {
    }

    public ObservableCollection<TaskRowViewModel> Items { get; } = [];

    public int OpenCount => Items.Count(t => !t.Done);

    public string SummaryText => Items.Count == 0 ? "还没有待办"
        : OpenCount == 0 ? "全部完成"
        : $"{OpenCount} 项未完成";

    /// <summary>The featured task list: "3 项未完成" or "全部完成".</summary>
    public string CompactText => OpenCount > 0 ? $"{OpenCount} 项待办" : "全部完成";

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => Items.Count == 0;

    public bool HasCompleted => Items.Any(t => t.Done);

    public void Attach(TaskStore store, bool enabled)
    {
        if (!ReferenceEquals(_store, store))
        {
            if (_store is not null)
            {
                _store.Changed -= Refresh;
            }

            _store = store;
            _store.Changed += Refresh;
        }

        SetStatus(enabled ? ModuleStatus.Ready : ModuleStatus.Disabled);
        Refresh();
    }

    public bool Add(string? title) => _store?.Add(title) is not null;

    public void Toggle(Guid id)
    {
        var row = Items.FirstOrDefault(t => t.Id == id);
        if (row is not null)
        {
            _store?.SetDone(id, !row.Done);
        }
    }

    public void Delete(Guid id) => _store?.Delete(id);

    /// <summary>
    /// Renames a task. An empty or unchanged title is ignored rather than saved: editing a task and
    /// pressing Enter without typing anything should not blank it out.
    /// </summary>
    public void Rename(Guid id, string? title)
    {
        var row = Items.FirstOrDefault(t => t.Id == id);
        if (row is null)
        {
            return;
        }

        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == row.Title)
        {
            return;
        }

        _store?.Rename(id, trimmed);
    }

    public void Move(Guid id, int delta) => _store?.Move(id, delta);

    public void ClearCompleted() => _store?.ClearCompleted();

    /// <summary>Syncs rows with the store in place (reuse, insert, remove, move).</summary>
    private void Refresh()
    {
        var items = _store?.Items ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var existing = Items.Select((row, index) => (row, index)).FirstOrDefault(x => x.row.Id == items[i].Id);
            if (existing.row is null)
            {
                Items.Insert(i, new TaskRowViewModel(items[i]));
                continue;
            }

            if (existing.index != i)
            {
                Items.Move(existing.index, i);
            }

            existing.row.Set(items[i]);
        }

        while (Items.Count > items.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CompactText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(HasCompleted));
    }
}
