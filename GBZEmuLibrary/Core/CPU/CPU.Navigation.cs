using System;
using InsSet = GBZEmuLibrary.InstructionSet;
using InsCBSet = GBZEmuLibrary.CBInstructionSet;
using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        private void JumpTestRelative(int flag, bool condition)
        {
            const int RELATIVE_OVERFLOW = 0x7F; //TODO move if needed

            if (Helpers.TestBit(_registers.F, flag) == condition)
            {
                int relativeAddress = ReadByte(_pc++);

                if (relativeAddress > RELATIVE_OVERFLOW)
                {
                    relativeAddress -= 256;
                }

                _pc = (ushort)(_pc + relativeAddress);

                IncrementClock();
                return;
            }

            _pc++;

            IncrementClock();
        }

        private void JumpTestImmediate16Bit(int flag, bool condition)
        {
            if (Helpers.TestBit(_registers.F, flag) == condition)
            {
                _pc = Read16Bit(_pc);

                IncrementClock();
                return;
            }

            _pc += 2;

            IncrementClock(2);
        }

        private void JumpImmediate()
        {
            var relativeJump = (sbyte) ReadByte(_pc++);

            _pc = (ushort)(_pc + relativeJump);

            IncrementClock();
        }

        private void JumpImmediate16Bit()
        {
            _pc = Read16Bit(_pc);
            IncrementClock();
        }

        private void Call()
        {
            Push((ushort)(_pc + 2));
            _pc = Read16Bit(_pc);
        }

        private void CallTest(int flag, bool value)
        {
            if (TestFlag(flag) == value)
            {
                Call();
                return;
            }

            _pc += 2;

            IncrementClock(2);
        }

        private void Return()
        {
            Pop(out _pc);
            IncrementClock();
        }

        private void ReturnTest(int flag, bool value)
        {
            if (TestFlag(flag) == value)
            {
                Return();
                IncrementClock();
                return;
            }

            IncrementClock();
        }

        private void Restart(byte address)
        {
            Push(_pc);
            _pc = address;
        }

        private void Halt()
        {
            if (!_interruptHandler.InterruptsEnabled && PendingInterrupt())
            {
                _haltSkip = true;
            }
            else
            {
                _interruptHandler.Halted = true;
            }
        }
    }
}