using System;
using System.Linq;
using System.IO;
using System.Text;

using PDTools.Files.Textures.PS3;
using PDTools.Files.Textures.PSP;
using PDTools.Files.Textures;
using PDTools.Files.Models.PS3.ModelSet3;

using Syroot.BinaryData;
using System.Collections.Generic;

using CommandLine.Text;
using CommandLine;
using static PDTools.Files.Textures.TextureSet3;

namespace TextureSetConverter;

class Program
{
    public static bool isBatchConvert = false;
    private static int processedFiles = 0;
    public static string currentFileName;

    static void Main(string[] args)
    {
        Console.WriteLine("Gran Turismo Texture Set (TXS3) / PDI Texture (PDI0) Converter - 1.3.0");

        var p = Parser.Default.ParseArguments<ConvertToPngVerbs, ConvertToImgVerbs>(args);
        p.WithParsed<ConvertToPngVerbs>(ConvertToPng)
         .WithParsed<ConvertToImgVerbs>(ConvertToImg)
         .WithNotParsed(e => { });
    }

    public static void ConvertToPng(ConvertToPngVerbs verbs)
    {
        foreach (var file in verbs.InputPath)
        {
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                Console.WriteLine($"ERROR: File does not exist: {file}");
                continue;
            }

            FileAttributes attr = File.GetAttributes(file);
            if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
            {
                string[] files = Directory.GetFiles(file, "*.*", SearchOption.AllDirectories);
                foreach (string f in files)
                {
                    currentFileName = Path.GetFileName(f);
                    try
                    {
                        ConvertFileToPng(f, verbs.Format, verbs.AsDds);
                        processedFiles++;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($@"ERROR: Could not convert {currentFileName} : {e.Message}");
                    }
                }
            }
            else
            {
                currentFileName = Path.GetFileName(file);
                try
                {
                    ConvertFileToPng(file, verbs.Format, verbs.AsDds);
                    processedFiles++;
                }
                catch (Exception e)
                {
                    Console.WriteLine($@"ERROR: Could not convert {currentFileName} : {e.Message}");
                }
            }
        }

        Console.WriteLine($"Done, {processedFiles} files were converted.");
    }

    public static void ConvertToImg(ConvertToImgVerbs verbs)
    {
        var files = verbs.InputPath.ToList();

        // Multi-texture "texture set" represented on disk as a folder (e.g. env2.txs/ holding
        // car_select_env_1.3SXT.DXT1.png + car_select_env_2.3SXT.DXT1.png). Build one container
        // holding all of the folder's sub-textures, output as <folder>.dds.
        if (files.Count == 1 && Directory.Exists(files[0]))
        {
            BuildMultiTextureSet(files[0]);
            return;
        }

        // Faithful round-trip path: a single "<name>.<container>.<format>.png" carries the
        // original container + pixel format in its filename, so rebuild exactly that.
        if (files.Count == 1)
        {
            var (container, pixelFormat) = ParseFormatFlag(files[0]);
            if (container != null)
            {
                string outputDds = Path.ChangeExtension(files[0], ".dds");

                if (container == "Tpp1")
                {
                    var tpp1Set = new TextureSet3();
                    tpp1Set.AddTexture(PGLUGETextureInfo.CreateTpp1(files[0], ParsePspFormat(pixelFormat)));
                    tpp1Set.BuildTpp1File(outputDds);
                    Console.WriteLine($"Built Tpp1/{pixelFormat} -> {outputDds}");
                    return;
                }

                // TXS3 (PS3) / 3SXT (PSP): drive the existing build via the resolved options.
                verbs.Format = container == "TXS3" ? TextureConsoleType.PS3 : TextureConsoleType.PSP;
                verbs.CellFormat = container == "3SXT" ? pixelFormat : Ps3CellFormat(pixelFormat);
            }
        }

        TextureSet3 textureSet = new TextureSet3();

        foreach (var file in files)
        {
            if (!AddFileToTextureSet(textureSet, file, verbs))
            {
                Console.WriteLine($"ERROR: Failed to add file '{file}', aborting.");
                return;
            }
        }

        if (files.Count == 1)
        {
            // Embed the clean asset name (e.g. "point_color.png" / "custom_22.dds"), not the flagged
            // file name - PD's textures carry the bare name + their real extension, and the flag would
            // just bloat the tail. Only when the file actually carried a flag (otherwise keep whatever
            // the Create* helper set).
            if (textureSet.TextureInfos.Count == 1 && ParseFormatFlag(files[0]).container != null)
                textureSet.TextureInfos[0].Name = StripFlag(files[0]) + Path.GetExtension(files[0]).ToLowerInvariant();

            // The built TXS3 binary normally goes to "<name>.dds". A ".dds" editable input would have
            // the SAME name, so the binary would overwrite the source - emit ".txs3" for those instead.
            string outputExt = Path.GetExtension(files[0]).Equals(".dds", StringComparison.OrdinalIgnoreCase) ? ".txs3" : ".dds";
            string outputImg = Path.ChangeExtension(files[0], outputExt);
            if (verbs.Format == TextureConsoleType.PSP)
                textureSet.BuildPSPTextureSetFile(outputImg);
            else
                textureSet.BuildTextureSetFile(outputImg);
        }
        else
        {
            if (string.IsNullOrEmpty(verbs.OutputPath))
            {
                Console.WriteLine("ERROR: Please specify an output file name when building multiple textures into one texture set.");
                return;
            }

            Console.WriteLine("Building a texture set file with multiple textures is not yet supported.");
            return;
        }
    }

    /// <summary>
    /// Parses a "name.CONTAINER.FORMAT.png" filename flag into (container, pixelFormat),
    /// or (null, null) if the file isn't flagged.
    /// </summary>
    static (string container, string format) ParseFormatFlag(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path); // strips ".png"
        string[] parts = name.Split('.');
        if (parts.Length >= 3)
        {
            string container = parts[^2];
            string format = parts[^1];
            if (container is "TXS3" or "3SXT" or "Tpp1")
                return (container, format);
        }
        return (null, null);
    }

    static eSCE_GE_TPF ParsePspFormat(string s) => s switch
    {
        "8888" => eSCE_GE_TPF.SCE_GE_TPF_8888,
        "IDTEX4" => eSCE_GE_TPF.SCE_GE_TPF_IDTEX4,
        "IDTEX8" => eSCE_GE_TPF.SCE_GE_TPF_IDTEX8,
        "DXT1" => eSCE_GE_TPF.SCE_GE_TPF_DXT1,
        "DXT5" => eSCE_GE_TPF.SCE_GE_TPF_DXT5,
        _ => throw new NotSupportedException($"Unsupported PSP pixel format '{s}'."),
    };

    /// <summary>Maps a PS3 TXS3 flag pixel-format name to the CellFormat token the builder expects.</summary>
    static string Ps3CellFormat(string pixelFormat) => pixelFormat switch
    {
        "A8R8G8B8" or "D8R8G8B8" => "DXT10",
        "DXT1" => "DXT1",
        "DXT23" => "DXT3",
        "DXT45" => "DXT5",
        _ => pixelFormat,
    };

    /// <summary>Strips the ".CONTAINER.FORMAT" flag from a flagged PNG's base name (e.g.
    /// "car_select_env_1.3SXT.DXT1" -> "car_select_env_1").</summary>
    static string StripFlag(string pngPath)
    {
        string name = Path.GetFileNameWithoutExtension(pngPath);
        string[] parts = name.Split('.');
        if (parts.Length >= 3 && parts[^2] is "TXS3" or "3SXT" or "Tpp1")
            return string.Join('.', parts[..^2]);
        return name;
    }

    /// <summary>
    /// Builds a multi-texture TXS3/3SXT "set" from every flagged PNG inside <paramref name="folder"/>
    /// (sorted by name). All PNGs must share one container (TXS3 or 3SXT). Output is &lt;folder&gt;.dds.
    /// PD's sub-texture names end in ".dds", so we restore that from the (flag-stripped) PNG name.
    /// </summary>
    static void BuildMultiTextureSet(string folder)
    {
        var pngs = Directory.GetFiles(folder, "*.*")
                            .Where(f => Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase)
                                     || Path.GetExtension(f).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                            .ToList();
        if (pngs.Count == 0)
        {
            Console.WriteLine($"ERROR: no .png/.dds files in texture-set folder '{folder}'.");
            Environment.Exit(1);
            return;
        }

        string container = null;
        var set = new TextureSet3();
        foreach (var png in pngs)
        {
            var (cont, fmt) = ParseFormatFlag(png);
            if (cont == null)
            {
                Console.WriteLine($"ERROR: '{png}' has no .CONTAINER.FORMAT flag; can't add to the set.");
                Environment.Exit(1);
                return;
            }
            if (cont == "Tpp1")
            {
                Console.WriteLine("ERROR: Tpp1 textures can't be part of a multi-texture set.");
                Environment.Exit(1);
                return;
            }
            container ??= cont;
            if (cont != container)
            {
                Console.WriteLine($"ERROR: mixed containers in '{folder}' ({container} vs {cont}).");
                Environment.Exit(1);
                return;
            }

            var verbs = new ConvertToImgVerbs
            {
                Format = cont == "TXS3" ? TextureConsoleType.PS3 : TextureConsoleType.PSP,
                CellFormat = cont == "3SXT" ? fmt : Ps3CellFormat(fmt),
            };
            if (!AddFileToTextureSet(set, png, verbs))
            {
                Console.WriteLine($"ERROR: failed to add '{png}' to the set.");
                Environment.Exit(1);
                return;
            }
            set.TextureInfos[^1].Name = StripFlag(png) + ".dds";
        }

        string outputDds = Path.ChangeExtension(folder.TrimEnd('/', '\\'), ".dds");
        if (container == "TXS3")
            set.BuildTextureSetFile(outputDds);
        else
            set.BuildPSPTextureSetFile(outputDds);

        Console.WriteLine($"Built {container} multi-texture set ({pngs.Count} textures) -> {outputDds}");
    }

    static bool _texConvExists = false;
    static void ConvertFileToPng(string path, TextureConsoleType consoleType, bool asDds = false)
    {
        currentFileName = Path.GetFileName(path);

        string magic = GetFileMagic(path);
        switch (magic)
        {
            case "TXS3":
            case "3SXT":
                ProcessTextureSetToPng(path, consoleType, asDds);
                return;
            case "Tpp1":
                ProcessTpp1ToPng(path);
                return;
            case "STRB":
                ProcessStrobeFile(path);
                return;
            case "IDP0":
                ConvertPDITextureToPng(path);
                return;
            case "MDL3":
            case "3LDM":
                ProcessModelSetFile(path);
                return;
        }
    }

    public static void ProcessStrobeFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open);
        fs.Position = 0xB0;
        int offset = fs.ReadInt32();
        int count = fs.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            fs.Position = offset + (i * 0x10);
            int textureOffset = fs.ReadInt32();

            fs.Position = textureOffset;

            var texture = new TextureSet3();
            texture.FromStream(fs, TextureConsoleType.PS3);
            texture.ConvertToStandardFormat(path + $"_{i}.png");
        }
    }

    public static bool AddFileToTextureSet(TextureSet3 textureSet, string path, ConvertToImgVerbs verbs)
    {
        string ext = Path.GetExtension(path);
        bool isImage = ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
        bool isDds = ext.Equals(".dds", StringComparison.OrdinalIgnoreCase);

        if (verbs.Format == TextureConsoleType.PSP)
        {
            if (!isImage)
            {
                Console.WriteLine($"Skipped {path}, no operation to be done");
                return true;
            }

            try
            {
                PGLUGETextureInfo pspTexture = verbs.CellFormat.ToUpperInvariant() switch
                {
                    "IDTEX4" => PGLUGETextureInfo.CreateFromIndexed(path, eSCE_GE_TPF.SCE_GE_TPF_IDTEX4),
                    "IDTEX8" => PGLUGETextureInfo.CreateFromIndexed(path, eSCE_GE_TPF.SCE_GE_TPF_IDTEX8),
                    "DXT1" => PGLUGETextureInfo.CreateFromDXT(path, eSCE_GE_TPF.SCE_GE_TPF_DXT1),
                    "DXT5" => PGLUGETextureInfo.CreateFromDXT(path, eSCE_GE_TPF.SCE_GE_TPF_DXT5),
                    "DXT3" => throw new NotSupportedException("PSP DXT3 building is not supported. Use 8888, IDTEX4, IDTEX8, DXT1 or DXT5."),
                    _ => PGLUGETextureInfo.CreateFrom8888(path), // "8888" / default
                };
                textureSet.AddTexture(pspTexture);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"ERROR: Failed to build PSP texture from '{path}': {e.Message}");
                return false;
            }
        }

        if (verbs.Format != TextureConsoleType.PS3)
        {
            Console.WriteLine("ERROR: Only PS3 and PSP TXS3 can be created.");
            return false;
        }

        // Convert if valid extension
        if (isImage || isDds)
        {
            // A .dds round-trips losslessly via FromDDS (no texconv); only image inputs need texconv.
            if (isImage && !_texConvExists && !File.Exists(Path.Combine(AppContext.BaseDirectory, "texconv.exe")))
            {
                // Look next to the converter executable, not the caller's CWD, so this works
                // when launched by GTGPB (which doesn't set a working directory). Exit non-zero
                // so a missing dependency surfaces as a real error instead of a phantom success
                // (the caller saw exit 0 + no .dds = "did not produce expected output").
                Console.WriteLine("TexConv (image to DDS tool) is missing. Download it from https://github.com/microsoft/DirectXTex/releases and place it next to the tool.");
                Environment.Exit(1);
            }
            if (isImage)
                _texConvExists = true;

            CELL_GCM_TEXTURE_FORMAT format = 0;
            if (verbs.CellFormat.Equals("DXT1"))
                format = CELL_GCM_TEXTURE_FORMAT.CELL_GCM_TEXTURE_COMPRESSED_DXT1;
            else if (verbs.CellFormat.Equals("DXT3"))
                format = CELL_GCM_TEXTURE_FORMAT.CELL_GCM_TEXTURE_COMPRESSED_DXT23;
            else if (verbs.CellFormat.Equals("DXT5"))
                format = CELL_GCM_TEXTURE_FORMAT.CELL_GCM_TEXTURE_COMPRESSED_DXT45;
            else if (verbs.CellFormat.Equals("DXT10"))
                format = CELL_GCM_TEXTURE_FORMAT.CELL_GCM_TEXTURE_A8R8G8B8;
            else
            {
                Console.WriteLine("ERROR: DXT format is invalid or not provided. must be DXT1/DXT3/DXT5/DXT10.");
                return false;
            }

            var texture = new PGLUCellTextureInfo();
            texture.FormatBits = format;

            /*
            if (verbs.Swizzle)
                (texture.TextureRenderInfo as PGLUCellTextureInfo).FormatBits &= ~CELL_GCM_TEXTURE_FORMAT.CELL_GCM_TEXTURE_LN; // Remove the default linear flag, set up swizzle
            */

            var added = isDds ? texture.FromDDS(path, format) : texture.FromStandardImage(path, format);
            if (!added)
                return false;

            textureSet.AddTexture(texture);

            return true;
        }
        else
        {
            Console.WriteLine($"Skipped {path}, no operation to be done");
            return true;
        }
    }

    static void ProcessTextureSetToPng(string path, TextureConsoleType consoleType, bool asDds = false)
    {
        Console.WriteLine($"Converting {currentFileName} to {(asDds ? "dds" : "png")}.");

        var txs = new TextureSet3();
        txs.FromFile(path, consoleType);
        txs.ConvertToStandardFormat(Path.ChangeExtension(path, ".png"), asDds);

    }

    static void ProcessTpp1ToPng(string path)
    {
        Console.WriteLine($"Converting {currentFileName} (Tpp1) to png.");

        var txs = new TextureSet3();
        using (var fs = new FileStream(path, FileMode.Open))
            txs.FromTpp1Stream(fs);
        txs.ConvertToStandardFormat(Path.ChangeExtension(path, ".png"));
    }

    static void ConvertPDITextureToPng(string path)
    {
        var pdiTexture = new PDITexture();
        pdiTexture.FromFile(path);
        pdiTexture.TextureSet.ConvertToStandardFormat(Path.ChangeExtension(path, ".png"));

        Console.WriteLine($"Converted {currentFileName} to png.");
    }

    static void ProcessModelSetFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open);
        using var bs = new BinaryStream(fs);
        ModelSet3 set = ModelSet3.FromStream(bs);

        set.TextureSet.ConvertToStandardFormat(path);
    }

    static string GetFileMagic(string path)
    {
        using var fs = new FileStream(path, FileMode.Open);

        Span<byte> mBuf = stackalloc byte[4];
        fs.ReadExactly(mBuf);
        return Encoding.ASCII.GetString(mBuf);
    }
}

[Verb("convert-png", HelpText = "Converts any img/mdl3/strb to png.")]
public class ConvertToPngVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input file texture or folder.")]
    public IEnumerable<string> InputPath { get; set; }

    [Option('f', "format", HelpText = "TXS format. Currently supported is PS3/PS4/PSP. Defaults to PS3. Some textures may not work or output correctly.")]
    public TextureConsoleType Format { get; set; } = TextureConsoleType.PS3;

    [Option("dds", HelpText = "Dump PS3 (Cell) textures as .dds instead of .png (lossless for DXT). PSP textures always dump as .png.")]
    public bool AsDds { get; set; }
}

[Verb("convert-img", HelpText = "Converts any image to .img.")]
public class ConvertToImgVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input files for texture set")]
    public IEnumerable<string> InputPath { get; set; }

    [Option('o', "output", HelpText = "Output texture set file. Not required if only building a texture set with one texture")]
    public string OutputPath { get; set; }

    [Option('f', "format", HelpText = "TXS format (PS3/PSP). Optional when the input filename carries a .CONTAINER.FORMAT flag (e.g. name.3SXT.IDTEX8.png).")]
    public TextureConsoleType? Format { get; set; }

    [Option("pf", HelpText = "Pixel format. PS3: DXT1/DXT3/DXT5/DXT10 (default DXT5). PSP: 8888/IDTEX4/IDTEX8/DXT1/DXT5 (default 8888).")]
    public string CellFormat { get; set; } = "DXT5";
}
