using AwesomeAssertions;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Files.Contracts;
using Backroom.Testing;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>The name and type rules on their own (no containers).</summary>
public sealed class FileRulesTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData("Café.txt", "Café.txt")]
    [InlineData("family‍.png", "family‍.png")]
    public void FileName_WhenValid_IsNormalized(string name, string expected)
    {
        FileName.TryNormalize(name, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("tab\tname")]
    [InlineData("rtl‮exe.txt")]
    [InlineData("bom﻿.txt")]
    [InlineData(".")]
    public void FileName_WhenInvalid_IsRefused(string? name)
    {
        FileName.TryNormalize(name, out _).Should().BeFalse();
    }

    [Fact]
    public void FileName_WithAnUnpairedSurrogate_IsRefused()
    {
        FileName.TryNormalize("lone" + (char)0xD800 + ".txt", out _).Should().BeFalse();
    }

    [Fact]
    public void FileName_UnderTurkishCulture_KeepsDottedAndDotlessI()
    {
        using var culture = new CultureScope("tr-TR");

        FileName.TryNormalize("IŞIK-istanbul.txt", out var normalized).Should().BeTrue();

        normalized.Should().Be("IŞIK-istanbul.txt");
    }

    [Theory]
    [InlineData("IMAGE/PNG", "image/png", FileKind.Image)]
    [InlineData(" text/csv ", "text/csv", FileKind.Spreadsheet)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileKind.Document)]
    [InlineData("application/octet-stream", "application/octet-stream", FileKind.Other)]
    public void FileTypes_WhenAllowed_ResolveCaseInsensitively(string declared, string expected, FileKind kind)
    {
        using var culture = new CultureScope("tr-TR");

        FileTypes.TryGet(declared, out var type).Should().BeTrue();

        type!.ContentType.Should().Be(expected);
        type.Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("text/plain;charset=utf-8")]
    [InlineData(null)]
    public void FileTypes_WhenNotAllowed_AreRefused(string? declared)
    {
        FileTypes.TryGet(declared, out _).Should().BeFalse();
    }

    [Fact]
    public void Confirm_WhenAnOfficeDocumentIsAZipArchive_AcceptsTheDeclaredType()
    {
        FileTypes.TryGet("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", out var declared).Should().BeTrue();

        FileTypes.Confirm(declared!, [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]).Should().Be(declared);
    }

    [Fact]
    public void Confirm_WhenTextContainsANulByte_IsRefused()
    {
        FileTypes.TryGet("text/plain", out var declared).Should().BeTrue();

        FileTypes.Confirm(declared!, [0x68, 0x69, 0x00, 0x21]).Should().BeNull();
    }

    [Fact]
    public void Confirm_WhenOctetStreamIsAnExecutable_KeepsOctetStream()
    {
        FileTypes.TryGet("application/octet-stream", out var declared).Should().BeTrue();

        FileTypes.Confirm(declared!, "MZ\u0090\0"u8).Should().Be(declared);
    }
}
