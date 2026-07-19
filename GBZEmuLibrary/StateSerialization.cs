using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Allows stateful objects backed by resources, rather than ordinary fields, to define their snapshot payload.
    /// </summary>
    internal interface IStateSerializable
    {
        void WriteState(BinaryWriter writer);
        void ReadState(BinaryReader reader);
    }

    /// <summary>
    /// Serializes the private mutable machine graph without exposing subsystem implementation details publicly.
    /// </summary>
    internal static class StateSerialization
    {
        private const byte NullReference = 0;
        private const byte ExistingReference = 1;
        private const byte NewReference = 2;

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly object FieldCacheLock = new object();

        public static byte[] Write(params object[] roots)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var context = new WriteContext(writer);
                writer.Write(roots.Length);
                for (var index = 0; index < roots.Length; index++)
                {
                    context.WriteValue(roots[index], roots[index].GetType());
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static void Read(byte[] data, params object[] roots)
        {
            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream))
            {
                var rootCount = reader.ReadInt32();
                if (rootCount != roots.Length)
                {
                    throw new InvalidDataException("Save state root layout does not match this library version.");
                }

                var context = new ReadContext(reader);
                for (var index = 0; index < roots.Length; index++)
                {
                    var restored = context.ReadValue(roots[index].GetType(), roots[index]);
                    if (!ReferenceEquals(restored, roots[index]))
                    {
                        throw new InvalidDataException("Save state attempted to replace a machine-state root.");
                    }
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException("Save state contains unexpected trailing machine data.");
                }
            }
        }

        private static FieldInfo[] GetStateFields(Type type)
        {
            lock (FieldCacheLock)
            {
                if (FieldCache.TryGetValue(type, out var cached))
                {
                    return cached;
                }

                var fields = new List<FieldInfo>();
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    var declared = current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (var index = 0; index < declared.Length; index++)
                    {
                        var field = declared[index];
                        if (ShouldCapture(field))
                        {
                            fields.Add(field);
                        }
                    }
                }

                fields.Sort((left, right) => string.CompareOrdinal(GetFieldKey(left), GetFieldKey(right)));
                cached = fields.ToArray();
                FieldCache[type] = cached;
                return cached;
            }
        }

        private static bool ShouldCapture(FieldInfo field)
        {
            if (field.IsStatic || field.GetCustomAttribute<SaveStateIgnoreAttribute>() != null)
            {
                return false;
            }

            var fieldType = field.FieldType;
            if (typeof(Delegate).IsAssignableFrom(fieldType) ||
                typeof(IDictionary).IsAssignableFrom(fieldType) ||
                typeof(Stream).IsAssignableFrom(fieldType))
            {
                return false;
            }

            // Immutable constructor configuration is validated by the live object and need not be assigned on restore.
            return !field.IsInitOnly || (!fieldType.IsValueType && fieldType != typeof(string));
        }

        private static string GetFieldKey(FieldInfo field)
        {
            return field.DeclaringType.FullName + "::" + field.Name;
        }

        private static bool IsScalar(Type type)
        {
            return type.IsEnum || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
                   type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
                   type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(char);
        }

        private static void WriteScalar(BinaryWriter writer, object value, Type type)
        {
            if (type.IsEnum)
            {
                writer.Write(Convert.ToInt32(value));
            }
            else if (type == typeof(bool)) writer.Write((bool)value);
            else if (type == typeof(byte)) writer.Write((byte)value);
            else if (type == typeof(sbyte)) writer.Write((sbyte)value);
            else if (type == typeof(short)) writer.Write((short)value);
            else if (type == typeof(ushort)) writer.Write((ushort)value);
            else if (type == typeof(int)) writer.Write((int)value);
            else if (type == typeof(uint)) writer.Write((uint)value);
            else if (type == typeof(long)) writer.Write((long)value);
            else if (type == typeof(ulong)) writer.Write((ulong)value);
            else if (type == typeof(float)) writer.Write((float)value);
            else if (type == typeof(double)) writer.Write((double)value);
            else if (type == typeof(char)) writer.Write((char)value);
            else throw new InvalidDataException("Unsupported save-state scalar type: " + type.FullName);
        }

        private static object ReadScalar(BinaryReader reader, Type type)
        {
            if (type.IsEnum) return Enum.ToObject(type, reader.ReadInt32());
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(float)) return reader.ReadSingle();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(char)) return reader.ReadChar();
            throw new InvalidDataException("Unsupported save-state scalar type: " + type.FullName);
        }

        private sealed class WriteContext
        {
            private readonly BinaryWriter _writer;
            private readonly Dictionary<object, int> _references =
                new Dictionary<object, int>(ReferenceComparer.Instance);

            public WriteContext(BinaryWriter writer)
            {
                _writer = writer;
            }

            public void WriteValue(object value, Type type)
            {
                if (IsScalar(type))
                {
                    WriteScalar(_writer, value, type);
                    return;
                }

                if (type.IsValueType)
                {
                    WriteMembers(value, type);
                    return;
                }

                if (value == null)
                {
                    _writer.Write(NullReference);
                    return;
                }

                if (_references.TryGetValue(value, out var reference))
                {
                    _writer.Write(ExistingReference);
                    _writer.Write(reference);
                    return;
                }

                reference = _references.Count;
                _references.Add(value, reference);
                _writer.Write(NewReference);
                _writer.Write(reference);
                _writer.Write(value.GetType().FullName);

                if (type == typeof(string))
                {
                    _writer.Write((string)value);
                }
                else if (value is Array array)
                {
                    WriteArray(array);
                }
                else if (value is Queue<int> queue)
                {
                    _writer.Write(queue.Count);
                    foreach (var item in queue)
                    {
                        _writer.Write(item);
                    }
                }
                else if (value is IStateSerializable serializable)
                {
                    serializable.WriteState(_writer);
                }
                else
                {
                    WriteMembers(value, value.GetType());
                }
            }

            private void WriteMembers(object value, Type type)
            {
                var fields = GetStateFields(type);
                _writer.Write(fields.Length);
                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    _writer.Write(GetFieldKey(field));
                    _writer.Write(field.FieldType.FullName);
                    WriteValue(field.GetValue(value), field.FieldType);
                }
            }

            private void WriteArray(Array array)
            {
                _writer.Write(array.Rank);
                for (var dimension = 0; dimension < array.Rank; dimension++)
                {
                    _writer.Write(array.GetLength(dimension));
                }

                var elementType = array.GetType().GetElementType();
                _writer.Write(elementType.FullName);

                if (elementType == typeof(byte) && array.Rank == 1)
                {
                    _writer.Write((byte[])array);
                    return;
                }

                foreach (var item in array)
                {
                    if (elementType == typeof(Color))
                    {
                        var color = (Color)item;
                        _writer.Write(color.R);
                        _writer.Write(color.G);
                        _writer.Write(color.B);
                        _writer.Write(color.Index);
                        _writer.Write(color.SgbIndex);
                        _writer.Write(color.BGPriority);
                    }
                    else
                    {
                        WriteValue(item, elementType);
                    }
                }
            }
        }

        private sealed class ReadContext
        {
            private readonly BinaryReader _reader;
            private readonly Dictionary<int, object> _references = new Dictionary<int, object>();

            public ReadContext(BinaryReader reader)
            {
                _reader = reader;
            }

            public object ReadValue(Type type, object currentValue)
            {
                if (IsScalar(type))
                {
                    return ReadScalar(_reader, type);
                }

                if (type.IsValueType)
                {
                    var boxed = currentValue ?? Activator.CreateInstance(type);
                    ReadMembers(boxed, type);
                    return boxed;
                }

                var marker = _reader.ReadByte();
                if (marker == NullReference)
                {
                    return null;
                }

                var reference = _reader.ReadInt32();
                if (marker == ExistingReference)
                {
                    if (!_references.TryGetValue(reference, out var existing))
                    {
                        throw new InvalidDataException("Save state contains an invalid object reference.");
                    }

                    return existing;
                }

                if (marker != NewReference)
                {
                    throw new InvalidDataException("Save state contains an invalid reference marker.");
                }

                var runtimeTypeName = _reader.ReadString();
                if (currentValue == null || currentValue.GetType().FullName != runtimeTypeName)
                {
                    throw new InvalidDataException("Save state object layout does not match this running emulator.");
                }

                _references.Add(reference, currentValue);

                if (type == typeof(string))
                {
                    var value = _reader.ReadString();
                    _references[reference] = value;
                    return value;
                }

                if (currentValue is Array array)
                {
                    ReadArray(array);
                }
                else if (currentValue is Queue<int> queue)
                {
                    queue.Clear();
                    var count = ReadNonNegativeCount("queue");
                    for (var index = 0; index < count; index++)
                    {
                        queue.Enqueue(_reader.ReadInt32());
                    }
                }
                else if (currentValue is IStateSerializable serializable)
                {
                    serializable.ReadState(_reader);
                }
                else
                {
                    ReadMembers(currentValue, currentValue.GetType());
                }

                return currentValue;
            }

            private void ReadMembers(object value, Type type)
            {
                var fields = GetStateFields(type);
                var fieldCount = ReadNonNegativeCount("field");
                if (fieldCount != fields.Length)
                {
                    throw new InvalidDataException("Save state field layout does not match this library version.");
                }

                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    if (_reader.ReadString() != GetFieldKey(field) || _reader.ReadString() != field.FieldType.FullName)
                    {
                        throw new InvalidDataException("Save state field identity does not match this library version.");
                    }

                    var restored = ReadValue(field.FieldType, field.GetValue(value));
                    if (!field.IsInitOnly)
                    {
                        field.SetValue(value, restored);
                    }
                }
            }

            private void ReadArray(Array array)
            {
                var rank = ReadNonNegativeCount("array rank");
                if (rank != array.Rank)
                {
                    throw new InvalidDataException("Save state array rank does not match this library version.");
                }

                for (var dimension = 0; dimension < rank; dimension++)
                {
                    if (ReadNonNegativeCount("array length") != array.GetLength(dimension))
                    {
                        throw new InvalidDataException("Save state array length does not match this running emulator.");
                    }
                }

                var elementType = array.GetType().GetElementType();
                if (_reader.ReadString() != elementType.FullName)
                {
                    throw new InvalidDataException("Save state array element type does not match this library version.");
                }

                if (elementType == typeof(byte) && array.Rank == 1)
                {
                    var bytes = _reader.ReadBytes(array.Length);
                    if (bytes.Length != array.Length)
                    {
                        throw new EndOfStreamException("Save state ended inside a byte array.");
                    }

                    Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);
                    return;
                }

                var indices = new int[array.Rank];
                for (var linearIndex = 0; linearIndex < array.Length; linearIndex++)
                {
                    SetIndices(array, linearIndex, indices);
                    if (elementType == typeof(Color))
                    {
                        var color = new Color(_reader.ReadByte(), _reader.ReadByte(), _reader.ReadByte())
                        {
                            Index = _reader.ReadInt32(),
                            SgbIndex = _reader.ReadByte(),
                            BGPriority = _reader.ReadBoolean()
                        };
                        array.SetValue(color, indices);
                    }
                    else
                    {
                        var current = array.GetValue(indices);
                        array.SetValue(ReadValue(elementType, current), indices);
                    }
                }
            }

            private int ReadNonNegativeCount(string name)
            {
                var count = _reader.ReadInt32();
                if (count < 0)
                {
                    throw new InvalidDataException("Save state contains an invalid " + name + " count.");
                }

                return count;
            }

            private static void SetIndices(Array array, int linearIndex, int[] indices)
            {
                for (var dimension = array.Rank - 1; dimension >= 0; dimension--)
                {
                    var length = array.GetLength(dimension);
                    indices[dimension] = linearIndex % length;
                    linearIndex /= length;
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}
