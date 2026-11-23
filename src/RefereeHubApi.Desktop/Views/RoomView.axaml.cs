using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RefereeHubApi.Desktop.ViewModels;

namespace RefereeHubApi.Desktop.Views;

public partial class RoomView : UserControl
{
    private RoomViewModel? attached;

    public RoomView()
    {
        InitializeComponent();
    }

    private RoomViewModel? vm => DataContext as RoomViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (attached != null)
        {
            attached.Chat.CollectionChanged -= chatChanged;
            attached.Activity.CollectionChanged -= activityChanged;
        }

        attached = vm;

        if (attached != null)
        {
            attached.Chat.CollectionChanged += chatChanged;
            attached.Activity.CollectionChanged += activityChanged;
            Dispatcher.UIThread.Post(() =>
            {
                ChatScroll.ScrollToEnd();
                ActivityScroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    private void chatChanged(object? sender, NotifyCollectionChangedEventArgs e) => stickToEnd(ChatScroll);

    private void activityChanged(object? sender, NotifyCollectionChangedEventArgs e) => stickToEnd(ActivityScroll);

    // follow new lines only when already at the bottom, so scrolling back to read isn't interrupted.
    private static void stickToEnd(ScrollViewer scroll)
    {
        bool atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 40;
        if (atEnd)
            Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void settingsOpened(object? sender, EventArgs e) => vm?.ResetSettingsForm();

    private void itemOpened(object? sender, EventArgs e) => vm?.ResetItemForm();

    private void inviteClick(object? sender, RoutedEventArgs e) => InviteBox.Focus();

    private void inviteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && vm != null)
        {
            e.Handled = true;
            vm.InviteCommand.Execute(null);
        }
    }

    private void chatKeyDown(object? sender, KeyEventArgs e)
    {
        if (vm == null)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.SendChatCommand.Execute(null);
        }
        else if (e.Key == Key.Tab && vm.ShowHints)
        {
            // complete the command name from the first hint.
            e.Handled = true;
            string usage = vm.CommandHints[0].Usage;
            int space = usage.IndexOf(' ');
            vm.ChatInput = (space < 0 ? usage : usage[..space]) + " ";
            ChatBox.CaretIndex = vm.ChatInput.Length;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ChatInput = string.Empty;
        }
    }
}
