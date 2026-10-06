using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LoomLCI.Core.VisualFiles;
using LoomLCI.PdfWorker;

namespace LoomLCI.PdfWorker.Tests;

public sealed class PdfRenderWorkerTests
{
    [Fact]
    public async Task RealPdfRendersBoundedPngWithAspectRatio()
    {
        var root = CreateTempRoot();
        try
        {
            var pdfPath = Path.Combine(root, "sample áé ñ.pdf");
            CreateSimplePdf(pdfPath);

            var response = await RunAsync(new PdfRenderWorkerRequest(
                pdfPath,
                Page: 1,
                MaxWidth: 1200,
                MaxHeight: 1200));

            Assert.Equal("ok", response.Status);
            Assert.Equal(1, response.PageCount);
            Assert.Equal(1, response.Page);
            Assert.InRange(response.Width, 1, 1200);
            Assert.InRange(response.Height, 1, 1200);
            Assert.NotNull(response.PngBytes);
            Assert.InRange(response.PngBytes!.Length, 1, VisualFilesLimits.MaxImageBytes);

            var png = response.PngBytes.AsSpan();
            Assert.True(png[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.Equal(
                response.Width,
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4))));
            Assert.Equal(
                response.Height,
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4))));

            var ratio = response.Width / (double)response.Height;
            Assert.InRange(ratio, (612d / 792d) - 0.002, (612d / 792d) + 0.002);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedPdfReturnsInvalidPdfWithoutCrashingWorker()
    {
        var root = CreateTempRoot();
        try
        {
            var pdfPath = Path.Combine(root, "invalid.pdf");
            await File.WriteAllTextAsync(pdfPath, "%PDF-1.4\nnot a valid PDF");

            var response = await RunAsync(new PdfRenderWorkerRequest(
                pdfPath,
                Page: 1,
                MaxWidth: 1200,
                MaxHeight: 1200));

            Assert.Equal("invalid_pdf", response.Status);
            Assert.Null(response.PngBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PdfRenderWorkerResponse> RunAsync(PdfRenderWorkerRequest request)
    {
        var inputBytes = JsonSerializer.SerializeToUtf8Bytes(request);
        await using var input = new MemoryStream();
        await input.WriteAsync(inputBytes);
        await input.WriteAsync("\n"u8.ToArray());
        input.Position = 0;

        await using var output = new MemoryStream();
        await using var error = new MemoryStream();

        var exitCode = await PdfRenderWorkerProgram.RunAsync(input, output, error);
        Assert.Equal(0, exitCode);

        output.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<PdfRenderWorkerResponse>(
            output,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Assert.IsType<PdfRenderWorkerResponse>(response);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "LoomLCI.PdfRenderWorker.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateSimplePdf(string path)
    {
        const string stream = "BT\n/F1 12 Tf\n72 720 Td\n(Render test) Tj\nET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        };

        using var memory = new MemoryStream();
        using var writer = new StreamWriter(memory, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\n"
        };

        writer.Write("%PDF-1.4\n");
        writer.Flush();
        var offsets = new long[objects.Length + 1];

        for (var index = 0; index < objects.Length; index++)
        {
            var number = index + 1;
            offsets[number] = memory.Position;
            writer.Write($"{number} 0 obj\n{objects[index]}\nendobj\n");
            writer.Flush();
        }

        var xref = memory.Position;
        writer.Write($"xref\n0 {objects.Length + 1}\n");
        writer.Write("0000000000 65535 f \n");
        for (var number = 1; number <= objects.Length; number++)
        {
            writer.Write($"{offsets[number]:0000000000} 00000 n \n");
        }

        writer.Write(
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();

        File.WriteAllBytes(path, memory.ToArray());
    }
}
