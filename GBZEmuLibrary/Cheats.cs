using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies the hardware cheat-device format represented by a cheat entry.
    /// </summary>
    public enum CheatFormat
    {
        /// <summary>ROM-read substitution encoded for a Game Boy Game Genie.</summary>
        GameGenie,
        /// <summary>Periodic RAM write encoded for a Game Boy GameShark or Action Replay.</summary>
        GameSharkActionReplay
    }

    /// <summary>
    /// Identifies the physical bank family selected by a banked GameShark/Action Replay entry.
    /// </summary>
    public enum CheatBankType
    {
        /// <summary>A bank in cartridge-owned external SRAM.</summary>
        CartridgeRam,
        /// <summary>A bank in CGB internal work RAM.</summary>
        WorkRam
    }

    /// <summary>
    /// Describes one parsed, engine-neutral Game Boy cheat code.
    /// </summary>
    public sealed class CheatEntry
    {
        private CheatCollection _owner;

        /// <summary>
        /// Gets the normalized cheat code.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// Gets the cheat-device format.
        /// </summary>
        public CheatFormat Format { get; }

        /// <summary>
        /// Gets the logical Game Boy address affected by this entry.
        /// </summary>
        public ushort Address { get; }

        /// <summary>
        /// Gets the replacement byte for a ROM read or periodic RAM write.
        /// </summary>
        public byte Value { get; }

        /// <summary>
        /// Gets the optional original ROM byte required by a nine-character Game Genie code.
        /// </summary>
        public byte? CompareValue { get; }

        /// <summary>
        /// Gets the optional physical bank index selected by a banked GameShark/Action Replay code.
        /// See <see cref="BankType"/> for its memory family. A null value uses the currently visible mapping.
        /// </summary>
        public byte? Bank { get; }

        /// <summary>
        /// Gets whether a banked code targets cartridge SRAM or CGB work RAM.
        /// A null value means the code uses the currently visible mapping.
        /// </summary>
        public CheatBankType? BankType { get; }

        /// <summary>
        /// Gets whether this entry is currently enabled in its owning collection.
        /// </summary>
        public bool Enabled { get; internal set; }

        private CheatEntry(string code, CheatFormat format, ushort address, byte value, byte? compareValue, byte? bank, CheatBankType? bankType)
        {
            Code = code;
            Format = format;
            Address = address;
            Value = value;
            CompareValue = compareValue;
            Bank = bank;
            BankType = bankType;
        }

        /// <summary>
        /// Parses a six- or nine-character Game Genie code or an eight-character GameShark/Action Replay code.
        /// Hyphens and whitespace are ignored.
        /// </summary>
        /// <exception cref="FormatException">The code is malformed or describes an unsupported write target.</exception>
        public static CheatEntry Parse(string code)
        {
            if (!TryParse(code, out var entry))
            {
                throw new FormatException("The value is not a supported Game Boy Game Genie or GameShark/Action Replay code.");
            }

            return entry;
        }

        /// <summary>
        /// Attempts to parse a Game Boy Game Genie or GameShark/Action Replay code without throwing.
        /// </summary>
        public static bool TryParse(string code, out CheatEntry entry)
        {
            entry = null;
            if (!TryNormalize(code, out var compact))
            {
                return false;
            }

            if (compact.Length == 6 || compact.Length == 9)
            {
                var value = ParseByte(compact, 0);
                var encodedAddress = ParseWord(compact, 2);
                var address = (ushort)(((encodedAddress >> 4) | (encodedAddress << 12)) ^ 0xF000);
                if (address >= MemorySchema.ROM_END)
                {
                    return false;
                }

                byte? compareValue = null;
                if (compact.Length == 9)
                {
                    // The middle digit in the final group is a device validation nibble, not part of the compare byte.
                    var encodedCompare = (byte)((HexValue(compact[6]) << 4) | HexValue(compact[8]));
                    var rotatedCompare = (byte)((encodedCompare >> 2) | (encodedCompare << 6));
                    compareValue = (byte)(rotatedCompare ^ 0xBA);
                }

                var normalized = compact.Length == 6
                    ? compact.Substring(0, 3) + "-" + compact.Substring(3, 3)
                    : compact.Substring(0, 3) + "-" + compact.Substring(3, 3) + "-" + compact.Substring(6, 3);
                entry = new CheatEntry(normalized, CheatFormat.GameGenie, address, value, compareValue, null, null);
                return true;
            }

            if (compact.Length != 8)
            {
                return false;
            }

            var prefix = ParseByte(compact, 0);
            byte? bank;
            if (prefix == 0x01)
            {
                bank = null;
            }
            else if ((prefix & 0xF0) == 0x80)
            {
                bank = (byte)(prefix & 0x0F);
            }
            else if ((prefix & 0xF0) == 0x90 && (prefix & 0x0F) <= 7)
            {
                bank = (byte)(prefix & 0x0F);
            }
            else
            {
                return false;
            }

            var data = ParseByte(compact, 2);
            var lowAddress = ParseByte(compact, 4);
            var highAddress = ParseByte(compact, 6);
            var ramAddress = (ushort)(lowAddress | (highAddress << 8));
            if (!IsRamAddress(ramAddress))
            {
                return false;
            }

            CheatBankType? bankType = null;
            if ((prefix & 0xF0) == 0x80)
            {
                if (ramAddress < MemorySchema.EXTERNAL_RAM_START || ramAddress >= MemorySchema.EXTERNAL_RAM_END)
                {
                    return false;
                }

                bankType = CheatBankType.CartridgeRam;
            }
            else if ((prefix & 0xF0) == 0x90)
            {
                bankType = CheatBankType.WorkRam;
            }

            entry = new CheatEntry(compact, CheatFormat.GameSharkActionReplay, ramAddress, data, null, bank, bankType);
            return true;
        }

        private static bool TryNormalize(string code, out string compact)
        {
            compact = null;
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            var characters = new char[code.Length];
            var length = 0;
            for (var index = 0; index < code.Length; index++)
            {
                var character = code[index];
                if (character == '-' || char.IsWhiteSpace(character))
                {
                    continue;
                }

                if (HexValue(character) < 0)
                {
                    return false;
                }

                characters[length++] = char.ToUpperInvariant(character);
            }

            compact = new string(characters, 0, length);
            return true;
        }

        private static bool IsRamAddress(ushort address)
        {
            return address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.RESTRICTED_RAM_START ||
                   address >= MemorySchema.HIGH_RAM_START && address < MemorySchema.HIGH_RAM_END;
        }

        private static byte ParseByte(string value, int index)
        {
            return (byte)((HexValue(value[index]) << 4) | HexValue(value[index + 1]));
        }

        private static ushort ParseWord(string value, int index)
        {
            return (ushort)((HexValue(value[index]) << 12) |
                            (HexValue(value[index + 1]) << 8) |
                            (HexValue(value[index + 2]) << 4) |
                            HexValue(value[index + 3]));
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'A' && value <= 'F')
            {
                return value - 'A' + 10;
            }

            if (value >= 'a' && value <= 'f')
            {
                return value - 'a' + 10;
            }

            return -1;
        }

        internal CheatCollection Owner
        {
            get => _owner;
            set => _owner = value;
        }
    }

    /// <summary>
    /// Owns the ordered cheat entries attached to one emulator instance.
    /// </summary>
    public sealed class CheatCollection
    {
        private readonly List<CheatEntry> _entries = new List<CheatEntry>();
        private readonly IReadOnlyList<CheatEntry> _readOnlyEntries;
        private readonly Dictionary<ushort, List<CheatEntry>> _gameGenieEntries = new Dictionary<ushort, List<CheatEntry>>();
        private readonly List<CheatEntry> _gameSharkEntries = new List<CheatEntry>();

        /// <summary>
        /// Gets the entries in deterministic insertion order.
        /// </summary>
        public IReadOnlyList<CheatEntry> Entries => _readOnlyEntries;

        /// <summary>
        /// Gets the number of attached entries.
        /// </summary>
        public int Count => _entries.Count;

        internal CheatCollection()
        {
            _readOnlyEntries = _entries.AsReadOnly();
        }

        /// <summary>
        /// Parses and attaches a cheat code.
        /// </summary>
        public CheatEntry Add(string code, bool enabled = true)
        {
            return Add(CheatEntry.Parse(code), enabled);
        }

        /// <summary>
        /// Attaches a previously parsed entry. An entry can belong to only one collection at a time.
        /// </summary>
        public CheatEntry Add(CheatEntry entry, bool enabled = true)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (entry.Owner != null)
            {
                throw new InvalidOperationException("This cheat entry already belongs to an emulator.");
            }

            entry.Owner = this;
            entry.Enabled = enabled;
            _entries.Add(entry);

            if (entry.Format == CheatFormat.GameGenie)
            {
                if (!_gameGenieEntries.TryGetValue(entry.Address, out var addressEntries))
                {
                    addressEntries = new List<CheatEntry>();
                    _gameGenieEntries.Add(entry.Address, addressEntries);
                }

                addressEntries.Add(entry);
            }
            else
            {
                _gameSharkEntries.Add(entry);
            }

            return entry;
        }

        /// <summary>
        /// Enables or disables an attached entry. Disabling a RAM-write code does not undo bytes already written.
        /// </summary>
        public void SetEnabled(CheatEntry entry, bool enabled)
        {
            EnsureOwned(entry);
            entry.Enabled = enabled;
        }

        /// <summary>
        /// Removes an entry and disables any future substitutions or writes from it.
        /// </summary>
        public bool Remove(CheatEntry entry)
        {
            if (entry == null || entry.Owner != this)
            {
                return false;
            }

            _entries.Remove(entry);
            if (entry.Format == CheatFormat.GameGenie)
            {
                var addressEntries = _gameGenieEntries[entry.Address];
                addressEntries.Remove(entry);
                if (addressEntries.Count == 0)
                {
                    _gameGenieEntries.Remove(entry.Address);
                }
            }
            else
            {
                _gameSharkEntries.Remove(entry);
            }

            entry.Enabled = false;
            entry.Owner = null;
            return true;
        }

        /// <summary>
        /// Removes and disables every attached entry.
        /// </summary>
        public void Clear()
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                _entries[index].Enabled = false;
                _entries[index].Owner = null;
            }

            _entries.Clear();
            _gameGenieEntries.Clear();
            _gameSharkEntries.Clear();
        }

        internal byte ApplyGameGenie(int address, byte originalValue)
        {
            if (!_gameGenieEntries.TryGetValue((ushort)address, out var addressEntries))
            {
                return originalValue;
            }

            for (var index = 0; index < addressEntries.Count; index++)
            {
                var entry = addressEntries[index];
                if (entry.Enabled && (!entry.CompareValue.HasValue || entry.CompareValue.Value == originalValue))
                {
                    return entry.Value;
                }
            }

            return originalValue;
        }

        internal IReadOnlyList<CheatEntry> GameSharkEntries => _gameSharkEntries;

        private void EnsureOwned(CheatEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (entry.Owner != this)
            {
                throw new ArgumentException("The cheat entry does not belong to this emulator.", nameof(entry));
            }
        }
    }
}
