using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class NdefEditorTests
{
    /// <summary>A factory-fresh NTAG213 and an editor writing into it as the card editor does.</summary>
    private static (List<byte[]> Pages, NdefEditorViewModel Editor, Func<int> Writes) Ntag213()
    {
        var pages = Enumerable.Range(0, 45).Select(_ => new byte[4]).ToList();
        pages[0] = [0x04, 0x11, 0x22, 0xBF];
        pages[3] = [0xE1, 0x10, 0x12, 0x00];
        pages[4] = [0x03, 0x00, 0xFE, 0x00];
        pages[41] = [0x04, 0x00, 0x00, 0xFF];
        pages[43] = [0xFF, 0xFF, 0xFF, 0xFF];
        var writes = 0;
        NdefEditorViewModel? editor = null;
        editor = new NdefEditorViewModel(() => (pages, TagSpecificType.Ntag213), (area, cc) =>
        {
            writes++;
            if (cc is not null) pages[3] = cc;
            for (var i = 0; i < area.Length / 4; i++) pages[Type2Ndef.FirstPage + i] = area[(i * 4)..(i * 4 + 4)];
            // The memory reports the change back, as HexBuffer.Changed does.
            editor!.Refresh();
        });
        editor.Refresh();
        return (pages, editor, () => writes);
    }

    private static IReadOnlyList<NdefRecord> Message(List<byte[]> pages) =>
        Type2Ndef.Read(pages, TagSpecificType.Ntag213).Records ?? [];

    [Fact]
    public void ValidEditsGoStraightIntoTheMemory()
    {
        var (pages, editor, writes) = Ntag213();
        Assert.True(editor.CanEdit);
        editor.AddCommand.Execute(NdefRecordKind.Link);
        var link = Assert.Single(editor.Records);

        // A new link is not valid yet, so the memory keeps its (empty) message.
        Assert.Equal(0, writes());
        Assert.NotNull(link.Error);

        link.Uri = "https://example.com";
        Assert.Null(link.Error);
        var record = Assert.Single(Message(pages));
        Assert.True(Ndef.TryUri(record, out var uri));
        Assert.Equal("https://example.com", uri);
        // The editor's own write doesn't rebuild the list under the record being typed in.
        Assert.Same(link, Assert.Single(editor.Records));
        Assert.Equal(NdefRecordKind.Link, link.Kind);
    }

    [Fact]
    public void DraggingAndRemovingRewriteTheMessage()
    {
        var (pages, editor, _) = Ntag213();
        editor.AddCommand.Execute(NdefRecordKind.Text);
        editor.Records[0].Text = "first";
        editor.AddCommand.Execute(NdefRecordKind.Text);
        editor.Records[1].Text = "second";
        Assert.Equal(2, Message(pages).Count);

        editor.MoveItem(1, 0);
        editor.CommitOrder();
        Assert.True(Ndef.TryText(Message(pages)[0], out var text, out _));
        Assert.Equal("second", text);

        editor.RemoveCommand.Execute(editor.Records[0]);
        Assert.True(Ndef.TryText(Assert.Single(Message(pages)), out text, out _));
        Assert.Equal("first", text);
    }

    [Fact]
    public void AChangeMadeElsewhereReloadsTheList()
    {
        var (_, editor, _) = Ntag213();
        editor.AddCommand.Execute(NdefRecordKind.Text);
        editor.Records[0].Text = "typed";
        var before = editor.Records[0];

        // E.g. the hex editor: the memory changes, and the list is read from it again.
        editor.Refresh();
        Assert.NotSame(before, Assert.Single(editor.Records));
        Assert.Equal("typed", editor.Records[0].Text);
        Assert.Equal(NdefRecordKind.Text, editor.Records[0].Kind);
    }
}
