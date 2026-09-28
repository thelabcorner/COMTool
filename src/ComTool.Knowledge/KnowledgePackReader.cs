using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ComTool.Knowledge;

/// <summary>
/// Knowledge pack wire format. The pack is plain UTF-8 text: a <c>key=value</c>
/// header, one blank line, then fixed sections of tab-separated records. It is
/// dependency free and streamable, so the runtime needs no database engine and
/// never materialises a multi-megabyte document to answer one question.
/// </summary>
public static class KnowledgePackFormat
{
    public const string Magic = "comtool.knowledge.pack/1";
    public const string FormatId = "comtool.knowledge.pack/v1";
    public const int Version = 1;
    public const char FieldSeparator = '\t';
    public const string SectionPrefix = "##";

    public const string SectionInterfaces = "interfaces";
    public const string SectionImplemented = "implemented";
    public const string SectionMethods = "methods";
    public const string SectionParameters = "parameters";
    public const string SectionProperties = "properties";
    public const string SectionAccessors = "accessors";
    public const string SectionAccessorParameters = "accessor_parameters";
    public const string SectionEnums = "enums";
    public const string SectionEnumValues = "enum_values";
}

public sealed class KnowledgePackException : Exception
{
    public KnowledgePackException(
        string kind,
        string message,
        bool retryable = false,
        IReadOnlyList<string>? suggestedActions = null)
        : base(message)
    {
        Kind = kind;
        Retryable = retryable;
        SuggestedActions = suggestedActions;
    }

    public string Kind { get; }

    public bool Retryable { get; }

    public IReadOnlyList<string>? SuggestedActions { get; }
}

/// <summary>Where the authoritative inventory came from.</summary>
public sealed record KnowledgeSourceProvenance(
    string Kind,
    string Database,
    string DatabaseSha256,
    string Json,
    string JsonSha256,
    string? JsonOnDiskSha256Observed,
    bool JsonOnDiskMatchesManifest,
    string Manifest,
    string ExportGeneratedAtUtc,
    string InterfaceKinds,
    int SourceOutgoingInterfacesFlagged);

/// <summary>
/// Host scope of the inventory. The version stays <c>unknown</c> unless the
/// inventory manifest itself declares one: a COM type library does not report the
/// application version it was exported from, and the version of the machine that
/// built the pack proves nothing about that export.
/// </summary>
public sealed record KnowledgeHostProvenance(string Family, string Version, string VersionSource)
{
    public const string UnknownVersion = "unknown";

    public bool VersionKnown =>
        !string.IsNullOrWhiteSpace(Version)
        && !string.Equals(Version, UnknownVersion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Non-authoritative observation of the pack build machine.</summary>
public sealed record KnowledgeBuildEnvironment(
    string? IllustratorProduct,
    string? IllustratorProductVersion,
    bool Authoritative);

public sealed record KnowledgePackCounts(
    int Interfaces,
    int Methods,
    int MethodParameters,
    int Properties,
    int PropertyAccessors,
    int AccessorParameters,
    int Parameters,
    int ImplementedInterfaces,
    int Enums,
    int EnumValues);

public sealed record KnowledgePackHeader(
    string Format,
    int Version,
    KnowledgeSourceProvenance Source,
    KnowledgeHostProvenance Host,
    KnowledgeBuildEnvironment BuildEnvironment,
    KnowledgePackCounts Counts,
    long BodyBytes,
    string BodySha256);

public sealed record KnowledgeInterfaceInfo(string Name, string Kind, string? Guid);

public sealed record KnowledgeImplementedInterfaceInfo(
    string Coclass,
    string Interface,
    string? Flags);

public sealed record KnowledgeMethodInfo(
    int Id,
    string Interface,
    string Name,
    int? Dispid,
    string? InvokeKind,
    string? ReturnType,
    int? OptionalParameterCount);

public sealed record KnowledgeParameterInfo(int OwnerId, int Position, string? Name, string? Type, string? Flags);

public sealed record KnowledgePropertyInfo(int Id, string Interface, string Name, int? Dispid);

public sealed record KnowledgeAccessorInfo(int Id, int PropertyId, string? InvokeKind, string? ReturnType);

public sealed record KnowledgeEnumValueInfo(string EnumName, string Name, long? Value);

/// <summary>Hard bounds for a single knowledge read.</summary>
public sealed record KnowledgeScanOptions
{
    public static KnowledgeScanOptions Default { get; } = new();

    public int DefaultLimit { get; init; } = 20;

    public int MaxLimit { get; init; } = 200;

    public long MaxScanBytes { get; init; } = 8L * 1024 * 1024;

    public int MaxScannedRecords { get; init; } = 500_000;

    public int MaxRetainedRecords { get; init; } = 8_192;

    public int ResolveLimit(int? requested)
    {
        var limit = requested ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit)
        {
            throw new KnowledgePackException(
                "knowledge_limit_out_of_range",
                $"limit must be between 1 and {MaxLimit}.",
                suggestedActions: ["request_a_smaller_limit"]);
        }

        return limit;
    }
}

public sealed record KnowledgeScanStats(
    long BytesScanned,
    int RecordsScanned,
    int RecordsRetained,
    bool ScanBudgetExhausted,
    bool RetentionCapReached);

/// <summary>Filters applied during a pass. Unset filters accept the record.</summary>
public sealed record KnowledgeSelection
{
    public string? SymbolName { get; init; }

    public string? InterfaceName { get; init; }

    public string? ReturnTypeContains { get; init; }

    public int? Dispid { get; init; }

    public bool IncludeInterfaces { get; init; }

    public bool IncludeMethods { get; init; }

    public bool IncludeProperties { get; init; }

    public bool IncludeEnums { get; init; }

    public bool IncludeEnumValues { get; init; }
}

public sealed record KnowledgeSelectionResult(
    IReadOnlyList<KnowledgeInterfaceInfo> Interfaces,
    IReadOnlyList<KnowledgeMethodInfo> Methods,
    IReadOnlyList<KnowledgePropertyInfo> Properties,
    IReadOnlyList<string> Enums,
    IReadOnlyList<KnowledgeEnumValueInfo> EnumValues,
    bool RetentionCapReached);

/// <summary>
/// Seekable, byte-bounded multi-pass reader over a knowledge pack. Each section
/// scan rewinds to the canonical body while cumulative byte/record budgets stay
/// in force; a later pass attaches parameters and accessors only to members the
/// first pass accepted. Nothing here writes, and file packs are opened read-only.
/// </summary>
public sealed class KnowledgePackReader : IDisposable
{
    private const string EmbeddedResourceName =
        "ComTool.Knowledge.Assets.illustrator-com.knowledge-pack";

    private readonly Stream _stream;
    private readonly KnowledgeScanOptions _options;
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly byte[] _buffer;
    private byte[] _line = new byte[256];
    private int _bufferStart;
    private int _bufferEnd;
    private bool _endOfStream;
    private bool _disposed;

    private KnowledgePackReader(
        Stream stream,
        KnowledgeScanOptions options,
        KnowledgePackHeader header,
        long bytesConsumed)
    {
        _stream = stream;
        _options = options;
        Header = header;
        BytesScanned = bytesConsumed;
        _buffer = new byte[64 * 1024];
    }

    public KnowledgePackHeader Header { get; private set; }

    public long BytesScanned { get; private set; }

    public int RecordsScanned { get; private set; }

    public int RecordsRetained { get; private set; }

    public bool ScanBudgetExhausted { get; private set; }

    /// <summary>Opens the pack embedded in this assembly.</summary>
    public static KnowledgePackReader OpenEmbedded(KnowledgeScanOptions? options = null)
    {
        var stream = typeof(KnowledgePackReader).Assembly
            .GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new KnowledgePackException(
                "knowledge_pack_not_found",
                $"Embedded knowledge pack '{EmbeddedResourceName}' is missing.");
        }

        return Open(stream, options);
    }

    /// <summary>Opens a pack from a caller-supplied path, read-only.</summary>
    public static KnowledgePackReader OpenFile(string path, KnowledgeScanOptions? options = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new KnowledgePackException(
                "knowledge_pack_not_found",
                $"Knowledge pack was not found at '{full}'.");
        }

        return Open(
            new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan),
            options);
    }

    public static KnowledgePackReader Open(Stream stream, KnowledgeScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException(
                "Pack stream must be readable.",
                nameof(stream));
        }

        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "Knowledge pack streams must be seekable so bounded multi-pass " +
                "queries can re-read the canonical pack without buffering it.",
                nameof(stream));
        }

        var reader = new KnowledgePackReader(
            stream,
            options ?? KnowledgeScanOptions.Default,
            header: null!,
            bytesConsumed: 0);
        reader.Header = reader.ReadHeader();
        return reader;
    }

    /// <summary>
    /// Walks every record and checks the body against the digest the pack
    /// declares. Intended to run once per pack, not once per query.
    /// </summary>
    public void VerifyIntegrity()
    {
        ThrowIfDisposed();
        RewindToBody();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = Header.BodyBytes;
        var consumed = 0L;

        if (_bufferStart < _bufferEnd && remaining > 0)
        {
            var available = _bufferEnd - _bufferStart;
            var take = (int)Math.Min(available, remaining);
            hash.AppendData(_buffer, _bufferStart, take);
            _bufferStart += take;
            remaining -= take;
            consumed += take;
        }

        while (remaining > 0)
        {
            var requested = (int)Math.Min(_buffer.Length, remaining);
            var read = _stream.Read(_buffer, 0, requested);
            if (read <= 0)
            {
                throw new KnowledgePackException(
                    "knowledge_pack_integrity_mismatch",
                    $"Pack body ended after {consumed} bytes; header declares " +
                    $"{Header.BodyBytes} bytes.");
            }

            BytesScanned += read;
            if (BytesScanned > _options.MaxScanBytes)
            {
                ScanBudgetExhausted = true;
                throw new KnowledgePackException(
                    "knowledge_scan_budget_exhausted",
                    $"Knowledge integrity scan exceeded the " +
                    $"{_options.MaxScanBytes} byte budget.");
            }

            hash.AppendData(_buffer, 0, read);
            remaining -= read;
            consumed += read;
        }

        if (_bufferStart < _bufferEnd || _stream.ReadByte() != -1)
        {
            throw new KnowledgePackException(
                "knowledge_pack_integrity_mismatch",
                "Pack contains trailing bytes beyond the body length declared " +
                "in its header.");
        }

        var computed = Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
        if (!string.Equals(
                computed,
                Header.BodySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgePackException(
                "knowledge_pack_integrity_mismatch",
                $"Pack body digest {computed} does not match the declared " +
                $"{Header.BodySha256}.");
        }
    }

    /// <summary>First pass: retains the primary records the query needs.</summary>
    public KnowledgeSelectionResult Select(KnowledgeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var interfaces = new List<KnowledgeInterfaceInfo>();
        var methods = new List<KnowledgeMethodInfo>();
        var properties = new List<KnowledgePropertyInfo>();
        var enums = new List<string>();
        var enumValues = new List<KnowledgeEnumValueInfo>();
        var capReached = false;

        bool Retain()
        {
            if (RecordsRetained >= _options.MaxRetainedRecords)
            {
                capReached = true;
                ScanBudgetExhausted = true;
                return false;
            }

            RecordsRetained++;
            return true;
        }

        ScanRecords(KnowledgePackFormat.SectionInterfaces, fields =>
        {
            if (!selection.IncludeInterfaces)
 {
         return;
            }

            if (selection.InterfaceName is { } wanted
                && !string.Equals(Field(fields, 0), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Retain())
                return;

            interfaces.Add(new KnowledgeInterfaceInfo(
                Field(fields, 0),
                Field(fields, 1),
                NullableField(fields, 2)));
        });

        ScanRecords(KnowledgePackFormat.SectionMethods, fields =>
        {
            if (!selection.IncludeMethods)
         {
         return;
     }

            var owner = Field(fields, 1);
            if (selection.InterfaceName is { } wantedInterface
                && !string.Equals(owner, wantedInterface, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var name = Field(fields, 2);
            if (selection.SymbolName is { } wantedName
                && !string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (selection.Dispid is { } wantedDispid
                && !string.Equals(Field(fields, 3), wantedDispid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return;
            }

            if (selection.ReturnTypeContains is { } typeNeedle
                && (NullableField(fields, 6) is not { } returnType
                    || returnType.IndexOf(typeNeedle, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return;
            }

            if (!Retain())
                return;

            methods.Add(new KnowledgeMethodInfo(
                Int(fields, 0),
                owner,
                name,
                NullableInt(fields, 3),
                NullableField(fields, 4),
                NullableField(fields, 6),
                NullableInt(fields, 7)));
        });

        ScanRecords(KnowledgePackFormat.SectionProperties, fields =>
        {
            if (!selection.IncludeProperties)
            {
    return;
            }

    var owner = Field(fields, 1);
            if (selection.InterfaceName is { } wantedInterface
             && !string.Equals(owner, wantedInterface, StringComparison.OrdinalIgnoreCase))
            {
    return;
     }

       var name = Field(fields, 2);
       if (selection.SymbolName is { } wantedName
        && !string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
            {
   return;
       }

  if (selection.Dispid is { } wantedDispid
    && !string.Equals(Field(fields, 3), wantedDispid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
  {
   return;
  }

     if (!Retain())
         return;

         properties.Add(new KnowledgePropertyInfo(
           Int(fields, 0),
        owner,
         name,
        NullableInt(fields, 3)));
        });

 ScanRecords(KnowledgePackFormat.SectionEnums, fields =>
  {
      if (!selection.IncludeEnums)
      {
  return;
      }

     if (!Retain())
         return;

      enums.Add(Field(fields, 0));
        });

        ScanRecords(KnowledgePackFormat.SectionEnumValues, fields =>
    {
            if (!selection.IncludeEnumValues)
            {
      return;
            }

            if (selection.SymbolName is { } wantedName
  && !string.Equals(Field(fields, 1), wantedName, StringComparison.OrdinalIgnoreCase))
{
       return;
     }

 if (!Retain())
     return;

            enumValues.Add(new KnowledgeEnumValueInfo(
          Field(fields, 0),
   Field(fields, 1),
          NullableLong(fields, 2)));
        });

        return new KnowledgeSelectionResult(
            interfaces,
            methods,
            properties,
            enums,
            enumValues,
            capReached);
    }

    /// <summary>
    /// Reads the coclass-to-interface relationships captured by the type
    /// library. The default interface flag is preserved so callers can
    /// canonicalize coclasses without guessing from naming conventions.
    /// </summary>
    public IReadOnlyList<KnowledgeImplementedInterfaceInfo>
        LoadImplementedInterfaces()
    {
        var result = new List<KnowledgeImplementedInterfaceInfo>();

        ScanRecords(KnowledgePackFormat.SectionImplemented, fields =>
        {
            if (RecordsRetained >= _options.MaxRetainedRecords)
            {
                ScanBudgetExhausted = true;
                return;
            }

            RecordsRetained++;
            result.Add(new KnowledgeImplementedInterfaceInfo(
                Field(fields, 0),
                Field(fields, 1),
                NullableField(fields, 2)));
        });

        if (result.Count != Header.Counts.ImplementedInterfaces)
        {
            throw new KnowledgePackException(
                "knowledge_scan_budget_exhausted",
                "The implemented-interface relationship scan ended before " +
                "the pack-declared relationship count was retained.",
                suggestedActions: ["retry_with_default_knowledge_limits"]);
        }

        return result;
    }

    /// <summary>
    /// Second pass: attaches parameters and accessor shapes to the members the
    /// first pass accepted. Accessor parameters are loaded only on demand.
    /// </summary>
    public (IReadOnlyDictionary<int, List<KnowledgeParameterInfo>> MethodParameters,
        IReadOnlyDictionary<int, List<KnowledgeParameterInfo>> AccessorParameters,
        IReadOnlyDictionary<int, List<KnowledgeAccessorInfo>> Accessors) LoadMembers(
            IReadOnlySet<int> methodIds,
            IReadOnlySet<int> propertyIds,
            bool includeAccessorParameters)
    {
        var methodParameters = new Dictionary<int, List<KnowledgeParameterInfo>>();
        var accessorParameters = new Dictionary<int, List<KnowledgeParameterInfo>>();
        var accessors = new Dictionary<int, List<KnowledgeAccessorInfo>>();

   ScanRecords(KnowledgePackFormat.SectionParameters, fields =>
     {
       var owner = Int(fields, 0);
  if (!methodIds.Contains(owner))
        {
  return;
    }

            if (RecordsRetained >= _options.MaxRetainedRecords)
    {
        ScanBudgetExhausted = true;
            return;
 }

            RecordsRetained++;
  if (!methodParameters.TryGetValue(owner, out var list))
      {
         list = [];
    methodParameters[owner] = list;
        }

    list.Add(Parameter(fields));
        });

     ScanRecords(KnowledgePackFormat.SectionAccessors, fields =>
   {
        var propertyId = Int(fields, 1);
     if (!propertyIds.Contains(propertyId))
   {
   return;
   }

          if (RecordsRetained >= _options.MaxRetainedRecords)
            {
 ScanBudgetExhausted = true;
   return;
   }

       RecordsRetained++;
  if (!accessors.TryGetValue(propertyId, out var list))
        {
            list = [];
   accessors[propertyId] = list;
        }

        list.Add(new KnowledgeAccessorInfo(
      Int(fields, 0),
     propertyId,
  NullableField(fields, 4),
            NullableField(fields, 6)));
        });

        if (!includeAccessorParameters)
    {
return (methodParameters, accessorParameters, accessors);
        }

     ScanRecords(KnowledgePackFormat.SectionAccessorParameters, fields =>
  {
         var owner = Int(fields, 0);
          if (!accessors.ContainsKey(FindPropertyId(accessors, owner)))
   {
      return;
       }

  if (RecordsRetained >= _options.MaxRetainedRecords)
     {
    ScanBudgetExhausted = true;
  return;
            }

     RecordsRetained++;
   if (!accessorParameters.TryGetValue(owner, out var list))
            {
                list = [];
     accessorParameters[owner] = list;
            }

         list.Add(Parameter(fields));
        });

        return (methodParameters, accessorParameters, accessors);
    }

    private static int FindPropertyId(
        IReadOnlyDictionary<int, List<KnowledgeAccessorInfo>> accessors,
        int accessorId)
    {
        foreach (var pair in accessors)
        {
            foreach (var accessor in pair.Value)
            {
                if (accessor.Id == accessorId)
   {
   return pair.Key;
       }
   }
        }

        return -1;
    }

    private static KnowledgeParameterInfo Parameter(string[] fields) =>
        new(Int(fields, 0), Int(fields, 1), NullableField(fields, 2), NullableField(fields, 3), NullableField(fields, 4));

    /// <summary>Consumes every record, invoking <paramref name="visitor"/> per matching record.</summary>
    private void ScanRecords(string section, Action<string[]> visitor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentNullException.ThrowIfNull(visitor);
        RewindToBody();

        var current = string.Empty;
        while (true)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(KnowledgePackReader));
            }

            if (BytesScanned > _options.MaxScanBytes || RecordsScanned > _options.MaxScannedRecords)
            {
                ScanBudgetExhausted = true;
                return;
            }

            if (!TryReadLine(out var line))
 {
     return;
   }

   if (line.Length == 0)
    {
       continue;
            }

            if (line.StartsWith(KnowledgePackFormat.SectionPrefix, StringComparison.Ordinal))
            {
     current = line[KnowledgePackFormat.SectionPrefix.Length..];
          continue;
            }

            RecordsScanned++;
            if (!string.Equals(current, section, StringComparison.Ordinal))
            {
                continue;
            }

visitor(line.Split(KnowledgePackFormat.FieldSeparator));
      }
    }

    private void RewindToBody()
    {
        ThrowIfDisposed();

        _stream.Seek(0, SeekOrigin.Begin);
        _bufferStart = 0;
        _bufferEnd = 0;
        _endOfStream = false;

        Header = ReadHeader();
        if (BytesScanned > _options.MaxScanBytes)
        {
            ScanBudgetExhausted = true;
            throw new KnowledgePackException(
                "knowledge_scan_budget_exhausted",
                $"Knowledge scan exceeded the {_options.MaxScanBytes} byte budget.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(KnowledgePackReader));
    }

    private KnowledgePackHeader ReadHeader()
    {
 var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? magic = null;

        while (true)
        {
if (!TryReadLine(out var line))
      {
         throw new KnowledgePackException(
        "knowledge_pack_malformed",
    "Pack ended before the header terminator.");
       }

  if (line.Length == 0)
{
            break;
    }

    var separator = line.IndexOf('=');
       if (separator <= 0)
            {
    magic = line;
      continue;
       }

      values[line[..separator]] = line[(separator + 1)..];
        }

        if (!string.Equals(magic, KnowledgePackFormat.Magic, StringComparison.Ordinal))
{
 throw new KnowledgePackException(
                "knowledge_pack_malformed",
          $"Pack magic '{magic}' is not '{KnowledgePackFormat.Magic}'.");
        }

   var format = Required(values, "pack.format");
        if (!string.Equals(format, KnowledgePackFormat.FormatId, StringComparison.Ordinal))
{
  throw new KnowledgePackException(
       "knowledge_pack_unsupported_format",
$"Pack format '{format}' is not supported (expected "
  + $"'{KnowledgePackFormat.FormatId}').");
        }

        Header = new KnowledgePackHeader(
       format,
      Int32Required(values, "pack.version"),
     new KnowledgeSourceProvenance(
   Required(values, "pack.provenance.source.kind"),
       Required(values, "pack.provenance.source.database"),
        Required(values, "pack.provenance.source.databaseSha256"),
      Required(values, "pack.provenance.source.json"),
           Required(values, "pack.provenance.source.jsonSha256"),
          Optional(values, "pack.provenance.source.jsonOnDiskSha256Observed"),
    bool.Parse(Required(values, "pack.provenance.source.jsonOnDiskMatchesManifest")),
     Required(values, "pack.provenance.source.manifest"),
                Required(values, "pack.provenance.export.generatedAtUtc"),
                Required(values, "pack.provenance.export.interfaceKinds"),
      Int32Required(values, "pack.provenance.export.sourceOutgoingInterfacesFlagged")),
    new KnowledgeHostProvenance(
       Required(values, "pack.provenance.host.family"),
        Required(values, "pack.provenance.host.version"),
     Required(values, "pack.provenance.host.versionSource")),
            new KnowledgeBuildEnvironment(
  Optional(values, "pack.buildEnvironment.illustratorProduct"),
         Optional(values, "pack.buildEnvironment.illustratorProductVersion"),
    bool.Parse(Required(values, "pack.buildEnvironment.authoritative"))),
    new KnowledgePackCounts(
       Int32Required(values, "pack.counts.interfaces"),
          Int32Required(values, "pack.counts.methods"),
    Int32Required(values, "pack.counts.methodParameters"),
          Int32Required(values, "pack.counts.properties"),
         Int32Required(values, "pack.counts.propertyAccessors"),
          Int32Required(values, "pack.counts.accessorParameters"),
    Int32Required(values, "pack.counts.parameters"),
            Int32Required(values, "pack.counts.implementedInterfaces"),
            Int32Required(values, "pack.counts.enums"),
                Int32Required(values, "pack.counts.enumValues")),
   long.Parse(Required(values, "pack.bodyBytes"), CultureInfo.InvariantCulture),
            Required(values, "pack.bodySha256"));

        return Header;
 }

    private bool TryReadLine(out string line)
 {
        var length = 0;
        while (true)
 {
      if (_bufferStart == _bufferEnd && !Fill())
      {
                line = length == 0 ? string.Empty : _utf8.GetString(_line, 0, length);
            return length > 0;
        }

      var span = _buffer.AsSpan(_bufferStart, _bufferEnd - _bufferStart);
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
         {
             EnsureLineCapacity(length + span.Length);
  span.CopyTo(_line.AsSpan(length));
            length += span.Length;
      _bufferStart = _bufferEnd;
    continue;
   }

          EnsureLineCapacity(length + newline);
       span[..newline].CopyTo(_line.AsSpan(length));
            length += newline;
            _bufferStart += newline + 1;
        line = _utf8.GetString(_line, 0, length);
        return true;
    }
    }

 private bool Fill()
    {
      if (_endOfStream)
   {
            return false;
        }

    _bufferStart = 0;
   _bufferEnd = _stream.Read(_buffer, 0, _buffer.Length);
   if (_bufferEnd <= 0)
        {
  _endOfStream = true;
     return false;
   }

        BytesScanned += _bufferEnd;
        if (BytesScanned > _options.MaxScanBytes)
      {
       ScanBudgetExhausted = true;
     }

        return true;
    }

    private void EnsureLineCapacity(int required)
    {
        if (_line.Length >= required)
     {
      return;
      }

        Array.Resize(ref _line, Math.Max(required, _line.Length * 2));
    }

    private static string Field(string[] fields, int index) => Unescape(fields[index]);

    private static string? NullableField(string[] fields, int index) =>
        fields[index].Length == 0 ? null : Unescape(fields[index]);

    private static int Int(string[] fields, int index) =>
        int.Parse(Field(fields, index), CultureInfo.InvariantCulture);

    private static int? NullableInt(string[] fields, int index) =>
        fields[index].Length == 0
            ? null
            : int.Parse(Field(fields, index), CultureInfo.InvariantCulture);

    private static long? NullableLong(string[] fields, int index) =>
        fields[index].Length == 0
            ? null
            : long.Parse(Field(fields, index), CultureInfo.InvariantCulture);

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
 return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
   {
          var current = value[i];
  if (current != '\\')
        {
      builder.Append(current);
            continue;
          }

      if (i + 1 >= value.Length)
   {
      throw new KnowledgePackException(
"knowledge_pack_malformed",
       "Pack field ends with a dangling escape character.");
       }

  i++;
            builder.Append(value[i] switch
       {
     '\\' => '\\',
                't' => '\t',
      'r' => '\r',
  'n' => '\n',
      var other => throw new KnowledgePackException(
           "knowledge_pack_malformed",
   $"Unsupported escape sequence '\\{other}' in pack field."),
       });
        }

        return builder.ToString();
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && value.Length > 0
            ? value
 : throw new KnowledgePackException(
       "knowledge_pack_malformed",
           $"Pack header is missing required key '{key}'.");

    private static string? Optional(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static int Int32Required(IReadOnlyDictionary<string, string> values, string key) =>
     int.Parse(Required(values, key), CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
{
         return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
