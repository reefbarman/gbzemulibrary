using System;

namespace GBZEmuLibrary
{
    internal class Joypad
    {
        private const int DIRECTION_BUTTONS_SELECT = 4;
        private const int OTHER_BUTTONS_SELECT = 5;

        private byte _joyPadState = 0xFF;
        private byte _joyPadRegister;

        public void WriteByte(byte data, int address)
        {
            _joyPadRegister = data;
        }

        public byte ReadByte(int address)
        {
            var state = _joyPadRegister ^ 0xFF;

            if (!Helpers.TestBit(_joyPadRegister, DIRECTION_BUTTONS_SELECT))
            {
                var directionButtonState = _joyPadState & 0xF;
                directionButtonState |= 0xF0;
                state &= directionButtonState;
            }
            else if (!Helpers.TestBit(_joyPadRegister, OTHER_BUTTONS_SELECT))
            {
                var otherButtonState = _joyPadState >> 4;
                otherButtonState |= 0xF0;     
                state &= otherButtonState; 
            }

            return (byte)state;
        }

        public void ButtonDown(JoypadButtons button)
        {
            var previousState = !Helpers.TestBit(_joyPadState, (int)button);

            Helpers.SetBit(ref _joyPadState, (int)button, false);

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

            if (requestInterrupt)
            {
                MessageBus.Instance.RequestInterrupt(Interrupts.Joypad);
            }
        }

        public void ButtonUp(JoypadButtons button)
        {
            Helpers.SetBit(ref _joyPadState, (int)button, true);
        }
    }
}
