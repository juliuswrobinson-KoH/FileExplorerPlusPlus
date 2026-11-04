using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace UI.Views;

public static class HexEditorDialog
{
    // Number of bytes displayed per page. The row formatter below uses 16 bytes per line.
    private const int PageSize = 1024;

    public static async Task ShowAsync(XamlRoot xamlRoot, Stream stream, long length, string displayName, bool readOnly, long regionOffset = 0)
    {
        var pageOffset = 0L;
        var originalBytes = Array.Empty<byte>();
        // Adjust these bounds to change the initial window size. The window remains resizable.
        var contentWidth = Math.Clamp(xamlRoot.Size.Width - 64, 720, 800);
        var contentHeight = Math.Clamp(xamlRoot.Size.Height - 200, 320, 900);
        var window = new Window
        {
            Title = readOnly ? $"Hex view — {Path.GetFileName(displayName)} (read-only)" : $"Hex editor — {Path.GetFileName(displayName)}",
        };
        var content = new Grid
        {
            MinWidth = 720,
            MinHeight = 320,
            RowSpacing = 8,
            Padding = new Thickness(16)
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var location = new TextBlock { Foreground = Brush(Microsoft.UI.Colors.LightSkyBlue) };
        var dataColumns = new Grid { ColumnSpacing = 12 };
        // Keep the hex dump and ASCII in adjacent columns, with extra room for offsets and byte values.
        dataColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        dataColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var hexColumn = new Grid { RowSpacing = 4 };
        hexColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        hexColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var hexHeader = new TextBlock { Text = "Offset and hexadecimal bytes" };
        Grid.SetRow(hexHeader, 0);
        hexColumn.Children.Add(hexHeader);
        var hexText = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            MinHeight = 120,
            IsReadOnly = readOnly,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false
        };
        Grid.SetRow(hexText, 1);
        hexColumn.Children.Add(hexText);
        
        dataColumns.Children.Add(hexColumn);
        var status = new TextBlock { Foreground = Brush(Microsoft.UI.Colors.LightGray) };
        var confirmation = new Border
        {
            Padding = new Thickness(10),
            Background = Brush(Microsoft.UI.Colors.DarkRed),
            Visibility = Visibility.Collapsed
        };
        var confirmationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmationButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var cancelWrite = new Button { Content = "Cancel write" };
        var confirmWrite = new Button { Content = "Confirm write" };
        confirmationButtons.Children.Add(cancelWrite);
        confirmationButtons.Children.Add(confirmWrite);
        var confirmationContent = new StackPanel();
        confirmationContent.Children.Add(confirmationText);
        confirmationContent.Children.Add(confirmationButtons);
        confirmation.Child = confirmationContent;

        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var previous = new Button { Content = "Previous page" };
        var next = new Button { Content = "Next page" };
        navigation.Children.Add(previous);
        navigation.Children.Add(next);
        Grid.SetRow(location, 0);
        Grid.SetRow(dataColumns, 1);
        Grid.SetRow(status, 2);
        Grid.SetRow(confirmation, 3);
        Grid.SetRow(navigation, 4);
        content.Children.Add(location);
        content.Children.Add(dataColumns);
        content.Children.Add(status);
        content.Children.Add(confirmation);
        content.Children.Add(navigation);
        window.Content = content;
        var handle = WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        var presenter = OverlappedPresenter.Create();
        // Use a normal desktop window so users can resize it to expose both columns.
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        appWindow.SetPresenter(presenter);
        appWindow.Resize(new SizeInt32(
            (int)Math.Min(contentWidth + 32, Math.Max(720, xamlRoot.Size.Width)),
            (int)Math.Min(contentHeight + 80, Math.Max(400, xamlRoot.Size.Height))));

        async Task LoadPageAsync()
        {
            confirmation.Visibility = Visibility.Collapsed;
            var count = (int)Math.Min(PageSize, Math.Max(0, length - pageOffset));
            originalBytes = new byte[count];
            stream.Seek(regionOffset + pageOffset, SeekOrigin.Begin);
            var bytesRead = 0;
            while (bytesRead < count)
            {
                var read = await stream.ReadAsync(originalBytes.AsMemory(bytesRead, count - bytesRead));
                if (read == 0)
                    break;
                bytesRead += read;
            }
            if (bytesRead != count)
                Array.Resize(ref originalBytes, bytesRead);
            location.Text = $"Offset 0x{regionOffset + pageOffset:X8}    {originalBytes.Length:N0} bytes on this page    {length:N0} bytes total";
            // Build rows with matching byte positions in the adjacent hex and ASCII columns.
            var display = FormatHexPage(originalBytes, regionOffset + pageOffset);
            hexText.Text = display.Hex;
            previous.IsEnabled = pageOffset > 0;
            next.IsEnabled = pageOffset + PageSize < length;
            status.Text = readOnly
                ? "This view is read-only."
                : "Edit the hexadecimal byte values. Keep the byte count unchanged; saving requires confirmation.";
        }


        previous.Click += async (_, _) =>
        {
            if (!TryParseHexPage(hexText.Text, originalBytes.Length, out var current) || !current.AsSpan().SequenceEqual(originalBytes))
            {
                status.Text = "Save or close without saving before changing pages.";
                return;
            }
            pageOffset = Math.Max(0, pageOffset - PageSize);
            await LoadPageAsync();
        };
        next.Click += async (_, _) =>
        {
            if (!TryParseHexPage(hexText.Text, originalBytes.Length, out var current) || !current.AsSpan().SequenceEqual(originalBytes))
            {
                status.Text = "Save or close without saving before changing pages.";
                return;
            }
            pageOffset = Math.Min(length, pageOffset + PageSize);
            await LoadPageAsync();
        };

        await LoadPageAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Activate();
        await closed.Task;
    }

    private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    private static HexPageDisplay FormatHexPage(IReadOnlyList<byte> bytes, long baseOffset)
    {
        // Hex rows include an 8-digit offset and up to 16 bytes; ASCII rows align by byte.
        var hexLines = new List<string>((bytes.Count + 15) / 16);
        for (var start = 0; start < bytes.Count; start += 16)
        {
            var count = Math.Min(16, bytes.Count - start);
            var hex = new System.Text.StringBuilder();
            hex.Append($"{baseOffset + start:X8}  ");
            for (var column = 0; column < count; column++)
            {
                if (column > 0)
                    hex.Append(' ');
                hex.Append(bytes[start + column].ToString("X2"));
            }
            hexLines.Add(hex.ToString() + "\t" + FormatAsciiLine(bytes, start, count));
        }

        return new HexPageDisplay(
            string.Join(Environment.NewLine, hexLines));
    }

    private static string FormatAsciiPage(IEnumerable<byte> bytes) =>
        string.Join(Environment.NewLine, bytes.Chunk(16).Select(row => FormatAsciiLine(row, 0, row.Length)));

    private static string FormatAsciiLine(IReadOnlyList<byte> bytes, int start, int count) =>
        new(Enumerable.Range(start, count)
            .Select(index => bytes[index] is >= 32 and <= 126 ? (char)bytes[index] : '.')
            .ToArray());

    private sealed record HexPageDisplay(string Hex);

    private static bool TryParseHexPage(string text, int expectedByteCount, out byte[] bytes)
    {
        var parsed = new List<byte>(expectedByteCount);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var hexColumn = line.Length >= 10 ? line[10..] : "";

            foreach (var value in hexColumn.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!byte.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedByte))
                {
                    bytes = [];
                    return false;
                }
                parsed.Add(parsedByte);
            }
        }

        if (parsed.Count != expectedByteCount)
        {
            bytes = [];
            return false;
        }

        bytes = parsed.ToArray();
        return true;
    }
}
