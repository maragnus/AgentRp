namespace AgentRp.Components.Layout;

using Microsoft.AspNetCore.Components;

public sealed class AppShellState
{
    public event Action? Changed;

    public MobileShellPane MobilePane { get; private set; } = MobileShellPane.Content;

    public DesktopSidebarMode DesktopSidebarMode { get; private set; } = DesktopSidebarMode.Wide;

    public bool IsSidebarVisibleOnMobile => MobilePane == MobileShellPane.Sidebar;

    public string MobileHeaderTitle { get; private set; } = "Agentic RP";

    public string? MobileHeaderSubtitle { get; private set; } = "Recent chats";

    public RenderFragment? DesktopHeaderAction { get; private set; }

    private DesktopSidebarMode LastVisibleDesktopSidebarMode { get; set; } = DesktopSidebarMode.Wide;

    public void SetDesktopHeaderAction(RenderFragment? action)
    {
        if (ReferenceEquals(DesktopHeaderAction, action))
            return;

        DesktopHeaderAction = action;
        Changed?.Invoke();
    }

    public void RefreshDesktopHeaderAction() => Changed?.Invoke();

    public void ShowSidebar()
    {
        if (MobilePane == MobileShellPane.Sidebar)
            return;

        MobilePane = MobileShellPane.Sidebar;
        Changed?.Invoke();
    }

    public void ShowContent()
    {
        if (MobilePane == MobileShellPane.Content)
            return;

        MobilePane = MobileShellPane.Content;
        Changed?.Invoke();
    }

    public void ToggleMobilePane()
    {
        if (IsSidebarVisibleOnMobile)
            ShowContent();
        else
            ShowSidebar();
    }

    public void SetDesktopSidebarMode(DesktopSidebarMode mode)
    {
        if (DesktopSidebarMode == mode)
            return;

        DesktopSidebarMode = mode;
        if (mode is not DesktopSidebarMode.Hidden)
            LastVisibleDesktopSidebarMode = mode;

        Changed?.Invoke();
    }

    public void ToggleDesktopSidebarMode()
    {
        var nextMode = DesktopSidebarMode switch
        {
            DesktopSidebarMode.Wide => DesktopSidebarMode.Narrow,
            DesktopSidebarMode.Narrow => DesktopSidebarMode.Hidden,
            _ => DesktopSidebarMode.Wide
        };

        SetDesktopSidebarMode(nextMode);
    }

    public void RestoreDesktopSidebar() => SetDesktopSidebarMode(LastVisibleDesktopSidebarMode);

    public void SetMobileContentHeader(string title, string? subtitle)
    {
        var nextTitle = string.IsNullOrWhiteSpace(title) ? "Content" : title;
        if (string.Equals(MobileHeaderTitle, nextTitle, StringComparison.Ordinal)
            && string.Equals(MobileHeaderSubtitle, subtitle, StringComparison.Ordinal))
            return;

        MobileHeaderTitle = nextTitle;
        MobileHeaderSubtitle = subtitle;
        Changed?.Invoke();
    }

    public void ApplyRouteDefaults(string? relativePath)
    {
        var path = relativePath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("chat/", StringComparison.OrdinalIgnoreCase))
        {
            ShowContent();
            if (!string.Equals(MobileHeaderTitle, "Chat", StringComparison.Ordinal)
                || MobileHeaderSubtitle is not null)
            {
                MobileHeaderTitle = "Chat";
                MobileHeaderSubtitle = null;
                Changed?.Invoke();
            }
            return;
        }

        ShowContent();
        if (!string.Equals(MobileHeaderTitle, "Content", StringComparison.Ordinal)
            || MobileHeaderSubtitle is not null)
        {
            MobileHeaderTitle = "Content";
            MobileHeaderSubtitle = null;
            Changed?.Invoke();
        }
    }
}

public enum MobileShellPane
{
    Sidebar,
    Content
}

public enum DesktopSidebarMode
{
    Narrow,
    Wide,
    Hidden
}
