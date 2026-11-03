using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace TradingHub.Feeds.Longbridge;

/// <summary>
/// Protobuf mínimo (varint, campos de longitud variable) para los mensajes de la API de 长桥 / Longbridge que usa la app.
/// Sin generar código: cada mensaje se arma y se lee a mano con los números de campo de openapi-protobufs.
/// </summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    private void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    public void Varint(ulong v)
    {
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        _ms.WriteByte((byte)v);
    }

    public void Int32(int field, int value)
    {
        if (value == 0) return;
        Tag(field, 0);
        Varint((ulong)(long)value);
    }

    public void Int64(int field, long value)
    {
        if (value == 0) return;
        Tag(field, 0);
        Varint((ulong)value);
    }

    public void Bool(int field, bool value)
    {
        if (!value) return;
        Tag(field, 0);
        Varint(1);
    }

    public void String(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Bytes(field, Encoding.UTF8.GetBytes(value));
    }

    public void Bytes(int field, byte[] value)
    {
        Tag(field, 2);
        Varint((ulong)value.Length);
        _ms.Write(value);
    }

    public void Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    /// <summary>repeated enum/int32 sin empaquetar (el servidor acepta ambas formas).</summary>
    public void RepeatedInt32(int field, IEnumerable<int> values)
    {
        foreach (var v in values)
        {
            Tag(field, 0);
            Varint((ulong)(long)v);
        }
    }

    public void MapStringString(int field, IReadOnlyDictionary<string, string>? map)
    {
        if (map == null) return;
        foreach (var (k, v) in map)
        {
            var entry = new ProtoWriter();
            entry.String(1, k);
            entry.String(2, v);
            Message(field, entry);
        }
    }
}

/// <summary>Lector de un mensaje protobuf: recorre los campos y deja que el que llama tome los que conoce.</summary>
public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Field { get; private set; }
    public int WireType { get; private set; }

    /// <summary>Siguiente campo; false al terminar.</summary>
    public bool Next()
    {
        if (_pos >= _data.Length) return false;
        ulong tag = ReadVarint();
        Field = (int)(tag >> 3);
        WireType = (int)(tag & 7);
        return Field > 0;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < _data.Length)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) break;
        }

        throw new InvalidDataException("protobuf: varint truncado");
    }

    public long Int64() => WireType == 0 ? (long)ReadVarint() : WireType == 1 ? ReadFixed64() : (long)ReadFixed32();
    public int Int32() => (int)Int64();
    public bool Bool() => ReadVarint() != 0;

    private long ReadFixed64()
    {
        long v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    private int ReadFixed32()
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    public ReadOnlySpan<byte> Bytes()
    {
        int len = (int)ReadVarint();
        if (_pos + len > _data.Length) throw new InvalidDataException("protobuf: campo truncado");
        var s = _data.Slice(_pos, len);
        _pos += len;
        return s;
    }

    public string String() => Encoding.UTF8.GetString(Bytes());

    public decimal Decimal() => decimal.TryParse(String(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Salta el campo actual (de cualquier tipo).</summary>
    public void Skip()
    {
        switch (WireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                _pos += 8;
                break;
            case 2:
                Bytes();
                break;
            case 5:
                _pos += 4;
                break;
            default:
                throw new InvalidDataException("protobuf: tipo de campo desconocido " + WireType);
        }
    }

    /// <summary>repeated int32 (empaquetado o no): devuelve los valores del campo actual.</summary>
    public List<int> PackedInt32()
    {
        var list = new List<int>();
        if (WireType == 2)
        {
            var packed = Bytes();
            var r = new ProtoReader(packed);
            while (r._pos < packed.Length) list.Add((int)r.ReadVarint());
        }
        else
        {
            list.Add(Int32());
        }

        return list;
    }
}

/// <summary>Comandos del socket de cotizaciones (quote/api.proto) y de control (control.proto).</summary>
public static class LbCmd
{
    public const byte Close = 0, Heartbeat = 1, Auth = 2, Reconnect = 3;
    public const byte Subscribe = 6, Unsubscribe = 7, QueryMarketTradeDay = 9, QuerySecurityStaticInfo = 10, QuerySecurityQuote = 11, QueryDepth = 14;
    public const byte QueryTrade = 17, QueryIntraday = 18, QueryCandlestick = 19, QueryHistoryCandlestick = 27;
    public const byte PushQuoteData = 101, PushDepthData = 102, PushBrokersData = 103, PushTradeData = 104;

    public const int SubQuote = 1, SubDepth = 2, SubBrokers = 3, SubTrade = 4;
}

/// <summary>Los mensajes que la app manda y lee (con sus números de campo).</summary>
public static class LbMessages
{
    public static byte[] AuthRequest(string token)
    {
        var w = new ProtoWriter();
        w.String(1, token);
        return w.ToArray();
    }

    public static byte[] ReconnectRequest(string sessionId)
    {
        var w = new ProtoWriter();
        w.String(1, sessionId);
        return w.ToArray();
    }

    /// <summary>AuthResponse / ReconnectResponse: (session_id, expires ms).</summary>
    public static (string SessionId, long Expires) ReadAuthResponse(ReadOnlySpan<byte> body)
    {
        string id = "";
        long expires = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: id = r.String(); break;
                case 2: expires = r.Int64(); break;
                default: r.Skip(); break;
            }
        }

        return (id, expires);
    }

    /// <summary>control.Error: (code, msg).</summary>
    public static (long Code, string Message) ReadError(ReadOnlySpan<byte> body)
    {
        long code = 0;
        string msg = "";
        try
        {
            var r = new ProtoReader(body);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1: code = r.Int64(); break;
                    case 2: msg = r.String(); break;
                    default: r.Skip(); break;
                }
            }
        }
        catch (InvalidDataException)
        {
            // cuerpo que no es un Error: se devuelve lo que se pudo leer
        }

        return (code, msg);
    }

    public static byte[] MultiSecurityRequest(IEnumerable<string> symbols)
    {
        var w = new ProtoWriter();
        foreach (var s in symbols) w.String(1, s);
        return w.ToArray();
    }

    public static byte[] SecurityRequest(string symbol)
    {
        var w = new ProtoWriter();
        w.String(1, symbol);
        return w.ToArray();
    }

    public static byte[] SubscribeRequest(IEnumerable<string> symbols, IEnumerable<int> subTypes, bool firstPush)
    {
        var w = new ProtoWriter();
        foreach (var s in symbols) w.String(1, s);
        w.RepeatedInt32(2, subTypes);
        w.Bool(3, firstPush);
        return w.ToArray();
    }

    public static byte[] UnsubscribeRequest(IEnumerable<string> symbols, IEnumerable<int> subTypes)
    {
        var w = new ProtoWriter();
        foreach (var s in symbols) w.String(1, s);
        w.RepeatedInt32(2, subTypes);
        return w.ToArray();
    }

    public static byte[] TradeRequest(string symbol, int count)
    {
        var w = new ProtoWriter();
        w.String(1, symbol);
        w.Int32(2, count);
        return w.ToArray();
    }

    /// <summary>SecurityCandlestickRequest (las últimas <paramref name="count"/> velas, máximo 1000).</summary>
    public static byte[] CandlestickRequest(string symbol, int period, int count, int tradeSession = 0)
    {
        var w = new ProtoWriter();
        w.String(1, symbol);
        w.Int32(2, period);
        w.Int32(3, count);
        w.Int32(4, 0); // sin ajuste
        w.Int32(5, tradeSession);
        return w.ToArray();
    }

    /// <summary>
    /// SecurityHistoryCandlestickRequest por desplazamiento: las <paramref name="count"/> velas anteriores (o posteriores)
    /// a una fecha y hora del mercado (yyyyMMdd / HHmm).
    /// </summary>
    public static byte[] HistoryCandlestickRequest(string symbol, int period, bool forward, string? date, string? minute, int count, int tradeSession = 0)
    {
        var offset = new ProtoWriter();
        offset.Int32(1, forward ? 1 : 0);
        offset.String(2, date);
        offset.String(3, minute);
        offset.Int32(4, count);
        var w = new ProtoWriter();
        w.String(1, symbol);
        w.Int32(2, period);
        w.Int32(3, 0);
        w.Int32(4, 1); // QUERY_BY_OFFSET
        w.Message(5, offset);
        w.Int32(7, tradeSession);
        return w.ToArray();
    }

    public readonly record struct LbCandle(long TimestampSec, decimal Open, decimal High, decimal Low, decimal Close, long Volume, decimal Turnover);

    /// <summary>SecurityCandlestickResponse → velas en orden cronológico.</summary>
    public static List<LbCandle> ReadCandlesticks(ReadOnlySpan<byte> body)
    {
        var list = new List<LbCandle>();
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 2 && r.WireType == 2) list.Add(ReadCandle(r.Bytes()));
            else r.Skip();
        }

        list.Sort((a, b) => a.TimestampSec.CompareTo(b.TimestampSec));
        return list;
    }

    private static LbCandle ReadCandle(ReadOnlySpan<byte> body)
    {
        decimal o = 0, h = 0, l = 0, c = 0, turnover = 0;
        long v = 0, t = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: c = r.Decimal(); break;
                case 2: o = r.Decimal(); break;
                case 3: l = r.Decimal(); break;
                case 4: h = r.Decimal(); break;
                case 5: v = r.Int64(); break;
                case 6: turnover = r.Decimal(); break;
                case 7: t = r.Int64(); break;
                default: r.Skip(); break;
            }
        }

        return new LbCandle(t, o, h, l, c, v, turnover);
    }

    /// <summary>Una operación del tape: dirección 0 = neutral, 1 = baja, 2 = sube.</summary>
    public readonly record struct LbTrade(decimal Price, long Volume, long TimestampSec, int Direction, string TradeType);

    /// <summary>SecurityTradeResponse o PushTrade: (símbolo, operaciones). El campo de las operaciones difiere (2 / 3).</summary>
    public static (string Symbol, List<LbTrade> Trades) ReadTrades(ReadOnlySpan<byte> body, bool push)
    {
        string symbol = "";
        var list = new List<LbTrade>();
        int field = push ? 3 : 2;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 1 && r.WireType == 2) symbol = r.String();
            else if (r.Field == field && r.WireType == 2) list.Add(ReadTrade(r.Bytes()));
            else r.Skip();
        }

        return (symbol, list);
    }

    private static LbTrade ReadTrade(ReadOnlySpan<byte> body)
    {
        decimal price = 0;
        long vol = 0, t = 0;
        int dir = 0;
        string type = "";
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: price = r.Decimal(); break;
                case 2: vol = r.Int64(); break;
                case 3: t = r.Int64(); break;
                case 4: type = r.String(); break;
                case 5: dir = r.Int32(); break;
                default: r.Skip(); break;
            }
        }

        return new LbTrade(price, vol, t, dir, type);
    }

    public readonly record struct LbLevel(int Position, decimal Price, long Volume, long Orders);

    /// <summary>SecurityDepthResponse (ask = 2, bid = 3) o PushDepth (ask = 3, bid = 4).</summary>
    public static (string Symbol, List<LbLevel> Asks, List<LbLevel> Bids) ReadDepth(ReadOnlySpan<byte> body, bool push)
    {
        string symbol = "";
        var asks = new List<LbLevel>();
        var bids = new List<LbLevel>();
        int askField = push ? 3 : 2, bidField = push ? 4 : 3;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 1 && r.WireType == 2) symbol = r.String();
            else if (r.Field == askField && r.WireType == 2) asks.Add(ReadLevel(r.Bytes()));
            else if (r.Field == bidField && r.WireType == 2) bids.Add(ReadLevel(r.Bytes()));
            else r.Skip();
        }

        return (symbol, asks, bids);
    }

    private static LbLevel ReadLevel(ReadOnlySpan<byte> body)
    {
        int pos = 0;
        decimal price = 0;
        long vol = 0, orders = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: pos = r.Int32(); break;
                case 2: price = r.Decimal(); break;
                case 3: vol = r.Int64(); break;
                case 4: orders = r.Int64(); break;
                default: r.Skip(); break;
            }
        }

        return new LbLevel(pos, price, vol, orders);
    }

    public sealed record LbStaticInfo(string Symbol, string NameCn, string NameEn, string NameHk, string Exchange, string Currency, int LotSize, string Board);

    public static List<LbStaticInfo> ReadStaticInfo(ReadOnlySpan<byte> body)
    {
        var list = new List<LbStaticInfo>();
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 1 && r.WireType == 2) list.Add(ReadStatic(r.Bytes()));
            else r.Skip();
        }

        return list;
    }

    private static LbStaticInfo ReadStatic(ReadOnlySpan<byte> body)
    {
        string symbol = "", cn = "", en = "", hk = "", ex = "", cur = "", board = "";
        int lot = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: symbol = r.String(); break;
                case 2: cn = r.String(); break;
                case 3: en = r.String(); break;
                case 4: hk = r.String(); break;
                case 6: ex = r.String(); break;
                case 7: cur = r.String(); break;
                case 8: lot = r.Int32(); break;
                case 17: board = r.String(); break;
                default: r.Skip(); break;
            }
        }

        return new LbStaticInfo(symbol, cn, en, hk, ex, cur, lot, board);
    }

    public readonly record struct LbQuote(string Symbol, decimal LastDone, decimal PrevClose, decimal Open, decimal High, decimal Low, long TimestampSec, long Volume, decimal Turnover, int TradeStatus);

    /// <summary>SecurityQuoteResponse (lista) o PushQuote (uno solo, con otros campos: se leen los comunes).</summary>
    public static List<LbQuote> ReadQuotes(ReadOnlySpan<byte> body)
    {
        var list = new List<LbQuote>();
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 1 && r.WireType == 2) list.Add(ReadQuote(r.Bytes()));
            else r.Skip();
        }

        return list;
    }

    public static LbQuote ReadQuote(ReadOnlySpan<byte> body)
    {
        string symbol = "";
        decimal last = 0, prev = 0, o = 0, h = 0, l = 0, turnover = 0;
        long t = 0, v = 0;
        int status = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: symbol = r.String(); break;
                case 2: last = r.Decimal(); break;
                case 3: prev = r.Decimal(); break;
                case 4: o = r.Decimal(); break;
                case 5: h = r.Decimal(); break;
                case 6: l = r.Decimal(); break;
                case 7: t = r.Int64(); break;
                case 8: v = r.Int64(); break;
                case 9: turnover = r.Decimal(); break;
                case 10: status = r.Int32(); break;
                default: r.Skip(); break;
            }
        }

        return new LbQuote(symbol, last, prev, o, h, l, t, v, turnover, status);
    }

    /// <summary>PushQuote: símbolo, último precio, timestamp, volumen acumulado y el de esta actualización (current_volume).</summary>
    public static (string Symbol, decimal LastDone, long TimestampSec, long Volume, long CurrentVolume) ReadPushQuote(ReadOnlySpan<byte> body)
    {
        string symbol = "";
        decimal last = 0;
        long t = 0, v = 0, cur = 0;
        var r = new ProtoReader(body);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: symbol = r.String(); break;
                case 3: last = r.Decimal(); break;
                case 7: t = r.Int64(); break;
                case 8: v = r.Int64(); break;
                case 12: cur = r.Int64(); break;
                default: r.Skip(); break;
            }
        }

        return (symbol, last, t, v, cur);
    }

    public static byte[] MarketTradeDayRequest(string market, string begDay, string endDay)
    {
        var w = new ProtoWriter();
        w.String(1, market);
        w.String(2, begDay);
        w.String(3, endDay);
        return w.ToArray();
    }

    public static (List<string> Days, List<string> HalfDays) ReadMarketTradeDays(ReadOnlySpan<byte> body)
    {
        var days = new List<string>();
        var half = new List<string>();
        var r = new ProtoReader(body);
        while (r.Next())
        {
            if (r.Field == 1 && r.WireType == 2) days.Add(r.String());
            else if (r.Field == 2 && r.WireType == 2) half.Add(r.String());
            else r.Skip();
        }

        return (days, half);
    }
}

/// <summary>
/// Paquetes del socket de 长桥 (versión 1, big endian): byte 0 = tipo (4 bits) | verify | gzip | reservado; byte 1 = comando;
/// pedido: id (4) + timeout ms (2) + largo del cuerpo (3); respuesta: id (4) + estado (1) + largo (3); push: largo (3).
/// </summary>
public static class LbPacket
{
    public const byte TypeRequest = 1, TypeResponse = 2, TypePush = 3;
    public const byte StatusSuccess = 0, StatusServerTimeout = 1, StatusClientTimeout = 2, StatusBadRequest = 3, StatusBadResponse = 4, StatusUnauthenticated = 5, StatusPermissionDenied = 6, StatusServerInternalError = 7;

    public static byte[] Request(byte cmd, uint requestId, ushort timeoutMs, ReadOnlySpan<byte> body)
    {
        var data = new byte[11 + body.Length];
        data[0] = TypeRequest;
        data[1] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2, 4), requestId);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), timeoutMs);
        WriteLen(data.AsSpan(8, 3), body.Length);
        body.CopyTo(data.AsSpan(11));
        return data;
    }

    /// <summary>Respuesta a un pedido del servidor (por ejemplo el eco de un latido).</summary>
    public static byte[] Response(byte cmd, uint requestId, byte status, ReadOnlySpan<byte> body)
    {
        var data = new byte[10 + body.Length];
        data[0] = TypeResponse;
        data[1] = cmd;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2, 4), requestId);
        data[6] = status;
        WriteLen(data.AsSpan(7, 3), body.Length);
        body.CopyTo(data.AsSpan(10));
        return data;
    }

    private static void WriteLen(Span<byte> s, int len)
    {
        s[0] = (byte)(len >> 16);
        s[1] = (byte)(len >> 8);
        s[2] = (byte)len;
    }

    public sealed record Parsed(byte Type, byte Cmd, uint RequestId, byte Status, byte[] Body);

    /// <summary>Lee un paquete completo (un mensaje binario del WebSocket).</summary>
    public static Parsed Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) throw new InvalidDataException("paquete demasiado corto");
        byte type = (byte)(frame[0] & 0x0F);
        bool gzip = (frame[0] & 0x20) != 0;
        bool verify = (frame[0] & 0x10) != 0;
        byte cmd = frame[1];
        int idx = 2;
        uint id = 0;
        byte status = 0;
        switch (type)
        {
            case TypeRequest:
                id = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(idx, 4));
                idx += 6; // id + timeout
                break;
            case TypeResponse:
                id = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(idx, 4));
                status = frame[idx + 4];
                idx += 5;
                break;
            case TypePush:
                break;
            default:
                throw new InvalidDataException("tipo de paquete desconocido " + type);
        }

        int len = (frame[idx] << 16) | (frame[idx + 1] << 8) | frame[idx + 2];
        idx += 3;
        if (frame.Length < idx + len) throw new InvalidDataException("paquete truncado");
        var body = frame.Slice(idx, len).ToArray();
        _ = verify; // la firma (nonce + firma) viene después del cuerpo y no se usa
        if (gzip)
        {
            using var src = new MemoryStream(body);
            using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
            using var dst = new MemoryStream();
            gz.CopyTo(dst);
            body = dst.ToArray();
        }

        return new Parsed(type, cmd, id, status, body);
    }
}

/// <summary>Avisos de la conexión (se muestran en el registro de la app si alguien se engancha).</summary>
public static class LongbridgeLog
{
    public static event Action<string>? Warning;

    public static void Warn(string message)
    {
        System.Diagnostics.Debug.WriteLine("[longbridge] " + message);
        Warning?.Invoke(message);
    }
}
