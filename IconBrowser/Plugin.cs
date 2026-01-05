using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Command;
using IconBrowser.Windows;

namespace IconBrowser;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "IconBrowser";
    private const string CommandName = "/iconbrowser";
    private const string CommandAlias = "/ib";

    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static ITextureProvider TextureProvider { get; private set; } = null!;
    public static ICommandManager CommandManager { get; private set; } = null!;
    public static IPluginLog Log { get; private set; } = null!;
    public static IDataManager DataManager { get; private set; } = null!;

    public WindowSystem WindowSystem { get; } = new("IconBrowser");
    private MainWindow MainWindow { get; init; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        ICommandManager commandManager,
        IPluginLog log,
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        TextureProvider = textureProvider;
        CommandManager = commandManager;
        Log = log;
        DataManager = dataManager;

        MainWindow = new MainWindow();
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Icon Browser window"
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Icon Browser window (alias)"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUI;
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUI;
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.IsOpen = true;
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    private void OpenMainUI()
    {
        MainWindow.IsOpen = true;
    }
}
