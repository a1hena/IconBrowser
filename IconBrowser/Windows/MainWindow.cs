using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using IconBrowser.Data;

namespace IconBrowser.Windows;

public class MainWindow : Window, IDisposable
{
    // Filter state
    private int _selectedCategoryIndex = 0; // 0 = All
    private int _selectedMajorPatchIndex = 0; // 0 = All
    private int _selectedMinorPatchIndex = 0; // 0 = All
    private string[] _categoryOptions = Array.Empty<string>();
    private string[] _majorPatchOptions = Array.Empty<string>();
    private List<string[]> _minorPatchOptionsByMajor = new();
    private List<List<PatchInfo>> _patchesByMajor = new();

    // Display settings
    private float _iconSize = 40;
    private bool _showHiRes = true;

    // Search
    private string _searchText = "";
    private int _searchIconId = -1;

    // Selection
    private int? _selectedIconId = null;

    // Texture cache
    private readonly Dictionary<int, ISharedImmediateTexture?> _textureCache = new();
    private readonly HashSet<int> _failedIcons = new();
    private const int MaxCachedTextures = 500;

    // Current displayed icons (filtered)
    private List<(int IconId, string Category, string PatchVersion)> _filteredIcons = new();
    private bool _needsRefresh = true;

    public MainWindow() : base("Icon Browser##IconBrowserMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        InitializeFilterOptions();
    }

    private void InitializeFilterOptions()
    {
        // Category options: "All" + actual categories
        var categories = PatchDataLoader.Categories;
        _categoryOptions = new string[categories.Length + 1];
        _categoryOptions[0] = "All Categories";
        for (var i = 0; i < categories.Length; i++)
            _categoryOptions[i + 1] = categories[i];

        // Group patches by major version (e.g., 2.x, 3.x, 4.x, 5.x, 6.x, 7.x)
        var allPatches = PatchDataLoader.Patches.OrderByDescending(p => p.Id).ToArray();
        var groupedByMajor = allPatches
            .GroupBy(p => GetMajorVersion(p.Version))
            .OrderByDescending(g => g.Key)
            .ToList();

        // Build major patch options
        _majorPatchOptions = new string[groupedByMajor.Count + 1];
        _majorPatchOptions[0] = "All Patches";
        _minorPatchOptionsByMajor.Clear();
        _patchesByMajor.Clear();

        // Add "All" entry for minor patches (index 0 corresponds to "All Patches")
        _minorPatchOptionsByMajor.Add(new[] { "All" });
        _patchesByMajor.Add(new List<PatchInfo>());

        for (var i = 0; i < groupedByMajor.Count; i++)
        {
            var majorVersion = groupedByMajor[i].Key;
            var patches = groupedByMajor[i].OrderByDescending(p => p.Id).ToList();

            _majorPatchOptions[i + 1] = $"{majorVersion}.x";
            _patchesByMajor.Add(patches);

            // Build minor patch options for this major version
            var minorOptions = new string[patches.Count + 1];
            minorOptions[0] = "All";
            for (var j = 0; j < patches.Count; j++)
            {
                minorOptions[j + 1] = $"{patches[j].Version} - {patches[j].Name}";
            }
            _minorPatchOptionsByMajor.Add(minorOptions);
        }
    }

    private static int GetMajorVersion(string version)
    {
        var dotIndex = version.IndexOf('.');
        if (dotIndex > 0 && int.TryParse(version.Substring(0, dotIndex), out var major))
            return major;
        return 0;
    }

    public void Dispose()
    {
        _textureCache.Clear();
        _failedIcons.Clear();
    }

    public override void Draw()
    {
        DrawFilterBar();
        ImGui.Separator();
        DrawSettingsBar();
        ImGui.Separator();

        if (_needsRefresh)
        {
            RefreshFilteredIcons();
            _needsRefresh = false;
        }

        DrawIconGrid();
        DrawSelectedIconInfo();
    }

    private void DrawFilterBar()
    {
        // Category filter
        ImGui.Text("Category:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo("##category", ref _selectedCategoryIndex, _categoryOptions, _categoryOptions.Length))
        {
            _needsRefresh = true;
        }

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Major Patch filter
        ImGui.Text("Patch:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("##majorPatch", ref _selectedMajorPatchIndex, _majorPatchOptions, _majorPatchOptions.Length))
        {
            _selectedMinorPatchIndex = 0; // Reset minor selection when major changes
            _needsRefresh = true;
        }

        // Minor Patch filter (only show if major patch is selected)
        if (_selectedMajorPatchIndex > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            var minorOptions = _minorPatchOptionsByMajor[_selectedMajorPatchIndex];
            if (ImGui.Combo("##minorPatch", ref _selectedMinorPatchIndex, minorOptions, minorOptions.Length))
            {
                _needsRefresh = true;
            }
        }

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Search box
        ImGui.Text("Search:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputTextWithHint("##search", "Icon ID", ref _searchText, 10))
        {
            if (int.TryParse(_searchText, out var id))
            {
                _searchIconId = id;
            }
            else
            {
                _searchIconId = -1;
            }
            _needsRefresh = true;
        }

        if (_searchIconId >= 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Go"))
            {
                _selectedIconId = _searchIconId;
            }
        }

        ImGui.SameLine();
        var spacing = ImGui.GetContentRegionAvail().X - 70;
        if (spacing > 0)
        {
            ImGui.Dummy(new Vector2(spacing, 0));
            ImGui.SameLine();
        }

        // Icon count
        ImGui.TextDisabled($"{_filteredIcons.Count} icons");
    }

    private void DrawSettingsBar()
    {
        ImGui.Text("Size:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.SliderFloat("##iconSize", ref _iconSize, 24, 80, "%.0f");

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        ImGui.Checkbox("HiRes", ref _showHiRes);
    }

    private void RefreshFilteredIcons()
    {
        _filteredIcons.Clear();

        // Get selected category (null = all)
        string? selectedCategory = _selectedCategoryIndex > 0 ? _categoryOptions[_selectedCategoryIndex] : null;

        // Get selected patches based on major/minor selection
        List<PatchInfo> selectedPatches;
        if (_selectedMajorPatchIndex == 0)
        {
            // All patches
            selectedPatches = _patchesByMajor.SelectMany(p => p).ToList();
        }
        else if (_selectedMinorPatchIndex == 0)
        {
            // All patches in selected major version
            selectedPatches = _patchesByMajor[_selectedMajorPatchIndex];
        }
        else
        {
            // Specific minor patch
            selectedPatches = new List<PatchInfo> { _patchesByMajor[_selectedMajorPatchIndex][_selectedMinorPatchIndex - 1] };
        }

        // If searching for specific icon ID
        if (_searchIconId >= 0)
        {
            var patchVersion = PatchDataLoader.GetIconPatchVersion(_searchIconId);
            if (patchVersion != null)
            {
                // Find category for this icon
                foreach (var category in PatchDataLoader.Categories)
                {
                    var patchId = PatchDataLoader.GetIconPatchId(category, _searchIconId);
                    if (patchId != null)
                    {
                        _filteredIcons.Add((_searchIconId, category, patchVersion));
                        break;
                    }
                }
            }
            else
            {
                // Icon not in patch data, just add it without category/patch info
                _filteredIcons.Add((_searchIconId, "Unknown", "Unknown"));
            }
            return;
        }

        // Build list based on filters
        var categories = selectedCategory != null ? new[] { selectedCategory } : PatchDataLoader.Categories;

        foreach (var category in categories)
        {
            foreach (var patch in selectedPatches)
            {
                var ranges = PatchDataLoader.GetIconRangesForPatch(category, patch.Id);
                foreach (var (start, end) in ranges)
                {
                    for (var iconId = start; iconId <= end; iconId++)
                    {
                        _filteredIcons.Add((iconId, category, patch.Version));
                    }
                }
            }
        }

        // Sort by icon ID
        _filteredIcons = _filteredIcons.OrderBy(x => x.IconId).ToList();

        // Limit to prevent performance issues
        if (_filteredIcons.Count > 5000)
        {
            _filteredIcons = _filteredIcons.Take(5000).ToList();
        }
    }

    private void DrawIconGrid()
    {
        // Calculate available space for the icon grid
        var selectedInfoHeight = _selectedIconId != null ? 110 : 0;
        var availableHeight = ImGui.GetContentRegionAvail().Y - selectedInfoHeight;

        using var child = ImRaii.Child("IconGrid", new Vector2(0, availableHeight), true);
        if (!child.Success) return;

        if (_filteredIcons.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No icons found. Try adjusting the filters.");
            return;
        }

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var effectiveIconSize = _iconSize * ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        // Calculate icons per row based on available width
        var iconsPerRow = Math.Max(1, (int)((availableWidth + spacing) / (effectiveIconSize + spacing)));

        var iconCount = 0;
        foreach (var (iconId, category, patchVersion) in _filteredIcons)
        {
            var texture = GetTexture(iconId);
            if (texture == null) continue;

            // Calculate proper size maintaining aspect ratio
            var texWidth = (float)texture.Width;
            var texHeight = (float)texture.Height;
            var aspectRatio = texWidth / texHeight;

            Vector2 displaySize;
            if (aspectRatio >= 1)
            {
                // Wider than tall
                displaySize = new Vector2(effectiveIconSize, effectiveIconSize / aspectRatio);
            }
            else
            {
                // Taller than wide
                displaySize = new Vector2(effectiveIconSize * aspectRatio, effectiveIconSize);
            }

            // Center the icon within the cell
            var cellSize = new Vector2(effectiveIconSize, effectiveIconSize);
            var offset = (cellSize - displaySize) / 2;

            var isSelected = _selectedIconId == iconId;
            var cursorPos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();

            // Draw selection highlight
            if (isSelected)
            {
                drawList.AddRectFilled(
                    cursorPos - new Vector2(2, 2),
                    cursorPos + cellSize + new Vector2(2, 2),
                    ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.8f)));
            }

            ImGui.PushID(iconId);

            // Use invisible button for the full cell area, then draw image centered
            var buttonPos = ImGui.GetCursorPos();
            if (ImGui.InvisibleButton($"btn{iconId}", cellSize))
            {
                _selectedIconId = iconId;
            }

            var isHovered = ImGui.IsItemHovered();

            // Draw the image centered within the cell
            var imagePos = cursorPos + offset;
            drawList.AddImage(texture.Handle, imagePos, imagePos + displaySize);

            // Draw hover border
            if (isHovered)
            {
                drawList.AddRect(
                    cursorPos - new Vector2(1, 1),
                    cursorPos + cellSize + new Vector2(1, 1),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f)),
                    0, ImDrawFlags.None, 2f);
            }

            ImGui.PopID();

            if (isHovered)
            {
                ImGui.BeginTooltip();
                ImGui.Text($"Icon ID: {iconId}");
                ImGui.Text($"Category: {category}");
                ImGui.Text($"Patch: {patchVersion}");
                ImGui.Text($"Size: {(int)texWidth}x{(int)texHeight}");
                ImGui.EndTooltip();
            }

            // Context menu
            if (ImGui.BeginPopupContextItem($"ctx{iconId}"))
            {
                if (ImGui.MenuItem($"Copy ID: {iconId}"))
                {
                    ImGui.SetClipboardText(iconId.ToString());
                }
                if (ImGui.MenuItem($"Copy ::{iconId}"))
                {
                    ImGui.SetClipboardText($"::{iconId}");
                }
                if (ImGui.MenuItem("Copy Path"))
                {
                    var folder = iconId / 1000 * 1000;
                    var path = $"ui/icon/{folder:D6}/{iconId:D6}.tex";
                    ImGui.SetClipboardText(path);
                }
                ImGui.EndPopup();
            }

            iconCount++;
            if (iconCount % iconsPerRow != 0)
                ImGui.SameLine();
        }
    }

    private void DrawSelectedIconInfo()
    {
        if (_selectedIconId == null) return;

        ImGui.Separator();
        using var child = ImRaii.Child("SelectedInfo", new Vector2(0, 100), true);
        if (!child.Success) return;

        var iconId = _selectedIconId.Value;
        var texture = GetTexture(iconId);

        if (texture != null)
        {
            // Display at proper aspect ratio
            var texWidth = (float)texture.Width;
            var texHeight = (float)texture.Height;
            var maxSize = 64 * ImGuiHelpers.GlobalScale;

            Vector2 displaySize;
            if (texWidth >= texHeight)
            {
                var scale = maxSize / texWidth;
                displaySize = new Vector2(maxSize, texHeight * scale);
            }
            else
            {
                var scale = maxSize / texHeight;
                displaySize = new Vector2(texWidth * scale, maxSize);
            }

            ImGui.Image(texture.Handle, displaySize);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Text($"Icon ID: {iconId}");
        ImGui.Text($"Hex: 0x{iconId:X6}");
        if (texture != null)
            ImGui.Text($"Size: {texture.Width}x{texture.Height}");

        // Show patch info
        var patchVersion = PatchDataLoader.GetIconPatchVersion(iconId);
        if (patchVersion != null)
        {
            var patchInfo = PatchDataLoader.GetPatch(patchVersion);
            ImGui.Text($"Patch: {patchVersion}" + (patchInfo != null ? $" ({patchInfo.Name})" : ""));
        }

        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.BeginGroup();

        if (ImGui.Button("Copy ID"))
            ImGui.SetClipboardText(iconId.ToString());
        ImGui.SameLine();

        if (ImGui.Button("Copy ::ID"))
            ImGui.SetClipboardText($"::{iconId}");
        ImGui.SameLine();

        if (ImGui.Button("Copy Path"))
        {
            var folder = iconId / 1000 * 1000;
            var path = $"ui/icon/{folder:D6}/{iconId:D6}.tex";
            ImGui.SetClipboardText(path);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _selectedIconId = null;
        }

        ImGui.EndGroup();
    }

    private IDalamudTextureWrap? GetTexture(int iconId)
    {
        // Skip icons that previously failed to load
        if (_failedIcons.Contains(iconId))
            return null;

        // Try to get cached ISharedImmediateTexture
        if (!_textureCache.TryGetValue(iconId, out var sharedTexture))
        {
            // Limit cache size
            if (_textureCache.Count > MaxCachedTextures)
            {
                var keysToRemove = _textureCache.Keys.Take(100).ToList();
                foreach (var key in keysToRemove)
                {
                    _textureCache.Remove(key);
                }
            }

            try
            {
                sharedTexture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup((uint)iconId, false, _showHiRes, null));
                _textureCache[iconId] = sharedTexture;
            }
            catch
            {
                _failedIcons.Add(iconId);
                return null;
            }
        }

        // Get the wrap each frame (don't cache the wrap itself)
        try
        {
            var wrap = sharedTexture?.GetWrapOrDefault();
            if (wrap == null)
            {
                return null;
            }
            return wrap;
        }
        catch
        {
            return null;
        }
    }
}
