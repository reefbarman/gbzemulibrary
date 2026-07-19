using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the active-low P1 joypad register and its two multiplexed four-button groups.
    /// </summary>
    internal sealed class Joypad : IMemoryUnit
    {
        private const int DIRECTION_BUTTONS_SELECT = 4;
        private const int OTHER_BUTTONS_SELECT = 5;
        private const byte BUTTON_GROUP_MASK = 0x0F;
        private const byte SELECT_MASK = 0x30;
        private const byte UNUSED_BITS = 0xC0;

        private readonly byte[] _joyPadStates = { 0xFF, 0xFF, 0xFF, 0xFF };
        private byte _joyPadRegister = SELECT_MASK;

        private readonly MessageBus _messageBus;
        private readonly SgbSystem _sgb;

        /// <summary>
        /// Creates a joypad connected to the interrupt bus for its owning emulator.
        /// </summary>
        public Joypad(MessageBus messageBus, SgbSystem sgb = null)
        {
            _messageBus = messageBus;
            _sgb = sgb;
        }

        /// <summary>
        /// Stores the two writable active-low button-group selection lines.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            var previous = _joyPadRegister;
            _joyPadRegister = (byte)(data & SELECT_MASK);
            _sgb?.WriteJoypad(_joyPadRegister, previous);
        }

        /// <summary>
        /// Returns whether this device owns the P1 joypad register.
        /// </summary>
        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.JOYPAD_REGISTER;
        }

        /// <summary>
        /// Reads selected active-low button lines while returning the two unused upper bits high.
        /// </summary>
        public byte ReadByte(int address)
        {
            var buttons = BUTTON_GROUP_MASK;
            var joyPadState = _joyPadStates[_sgb != null && _sgb.Enabled ? _sgb.CurrentPlayer : 0];

            if ((_joyPadRegister & SELECT_MASK) == SELECT_MASK && _sgb != null && _sgb.Enabled)
            {
                buttons = _sgb.GetPlayerId();
            }

            if (!Helpers.TestBit(_joyPadRegister, DIRECTION_BUTTONS_SELECT))
            {
                buttons &= (byte)(joyPadState & BUTTON_GROUP_MASK);
            }

            if (!Helpers.TestBit(_joyPadRegister, OTHER_BUTTONS_SELECT))
            {
                buttons &= (byte)(joyPadState >> 4);
            }

            return (byte)(UNUSED_BITS | _joyPadRegister | buttons);
        }

        /// <summary>
        /// Presses a button and requests a joypad interrupt when its selected input line falls.
        /// </summary>
        public void ButtonDown(JoypadButtons button)
        {
            ButtonDown(button, 0);
        }

        /// <summary>
        /// Presses a button for one of the four SGB controller slots.
        /// </summary>
        public void ButtonDown(JoypadButtons button, int player)
        {
            ValidatePlayer(player);
            var previousState = !Helpers.TestBit(_joyPadStates[player], (int)button);

            Helpers.SetBit(ref _joyPadStates[player], (int)button, false);

            var directionalButton = button <= JoypadButtons.Down;

            var requestInterrupt = false;

            if (directionalButton && !Helpers.TestBit(_joyPadRegister, DIRECTION_BUTTONS_SELECT))
            {
                requestInterrupt = !previousState;
            }
            else if (!directionalButton && !Helpers.TestBit(_joyPadRegister, OTHER_BUTTONS_SELECT))
            {
                requestInterrupt = !previousState;
            }

            var activePlayer = _sgb != null && _sgb.Enabled ? _sgb.CurrentPlayer : 0;
            if (requestInterrupt && player == activePlayer)
            {
                _messageBus.RequestInterrupt(Interrupts.Joypad);
            }
        }

        /// <summary>
        /// Releases a button by restoring its active-low input line high.
        /// </summary>
        public void ButtonUp(JoypadButtons button)
        {
            ButtonUp(button, 0);
        }

        /// <summary>
        /// Releases a button for one of the four SGB controller slots.
        /// </summary>
        public void ButtonUp(JoypadButtons button, int player)
        {
            ValidatePlayer(player);
            Helpers.SetBit(ref _joyPadStates[player], (int)button, true);
        }

        private static void ValidatePlayer(int player)
        {
            if (player < 0 || player >= 4)
            {
                throw new ArgumentOutOfRangeException(nameof(player));
            }
        }
    }
}
