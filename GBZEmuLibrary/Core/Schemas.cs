namespace GBZEmuLibrary
{
    public static class Sound
    {
        public const int SAMPLE_RATE = 44100;
    }

    public enum JoypadButtons
    {
        Right,
        Left,
        Up,
        Down,
        A,
        B,
        Select,
        Start,
        Count
    }

    public static class Display
    {
        public static Color[] DefaultPalette { get; } = {
            new Color(224, 248, 208),
            new Color(136, 192, 112),
            new Color(52, 104, 86),
            new Color(8, 24, 32)
        };
    }

    internal class GameBoySchema
    {
        public const int TARGET_FRAMERATE     = 60;
        public const int MAX_DMG_CLOCK_CYCLES = 4194304;
    }

    internal class MemorySchema
    {
        public const int BIOS_END = 0x100;

        public const int MAX_RAM_SIZE = 0x10000;

        public const int ROM_END                         = 0x8000;
        public const int VIDEO_RAM_START                 = ROM_END;
        public const int VIDEO_RAM_END                   = 0xA000;
        public const int EXTERNAL_RAM_START              = VIDEO_RAM_START;
        public const int EXTERNAL_RAM_END                = 0xC000;
        public const int WORK_RAM_START                  = EXTERNAL_RAM_END; //Made up of two banks of 4KB
        public const int WORK_RAM_END                    = 0xE000;
        public const int ECHO_RAM_START                  = WORK_RAM_END;
        public const int ECHO_RAM_END                    = 0xFE00;
        public const int SPRITE_ATTRIBUTE_TABLE_START    = ECHO_RAM_END;
        public const int SPRITE_ATTRIBUTE_TABLE_END      = 0xFEA0;
        public const int RESTRICTED_RAM_START            = SPRITE_ATTRIBUTE_TABLE_END;
        public const int RESTRICTED_RAM_END              = 0xFF00;
        public const int JOYPAD_REGISTER                 = RESTRICTED_RAM_END;
        public const int DIVIDE_REGISTER                 = 0xFF04;
        public const int TIMER_START                     = 0xFF05;
        public const int TIMA                            = TIMER_START;
        public const int TMA                             = 0xFF06;
        public const int TMC                             = 0xFF07;
        public const int TIMER_END                       = 0xFF08;
        public const int APU_REGISTERS_START             = 0xFF10;
        public const int APU_REGISTERS_END               = 0xFF40;
        public const int INTERRUPT_REQUEST_REGISTER      = 0xFF0F;
        public const int GPU_REGISTERS_START             = 0xFF40;
        public const int DMA_REGISTER                    = 0xFF46;
        public const int GPU_REGISTERS_END               = 0xFF4C;
        public const int HIGH_RAM_START                  = 0xFF80;
        public const int HIGH_RAM_END                    = 0xFFFF;
        public const int INTERRUPT_ENABLE_REGISTER_START = HIGH_RAM_END;
        public const int INTERRUPT_ENABLE_REGISTER_END   = MAX_RAM_SIZE;

        public const int WORK_RAM_ECHO_OFFSET      = 0x2000;
        public const int TILE_DATA_UNSIGNED_START  = 0x8000;
        public const int TILE_DATA_SIGNED_START    = 0x8800;
        public const int BACKGROUND_LAYOUT_0_START = 0x9800;
        public const int BACKGROUND_LAYOUT_1_START = 0x9C00;
    }

    internal class CartridgeSchema
    {
        public const int MAX_CART_SIZE     = 0x200000; //Max Cart Size of 2MB
        public const int MAX_CART_RAM_SIZE = 0x8000;

        public const int ROM_BANK_SIZE     = 0x4000;
        public const int ROM_BANK_ZERO_LOC = 0;

        public const int RAM_BANK_SIZE = 0x2000;

        // Cart ROM Header Schema
        public const int MBC_MODE_LOC = 0x147;
    }

    internal class APUSchema
    {
        public const int CHANNEL_LEFT  = 1;
        public const int CHANNEL_RIGHT = 2;
        public const int CHANNEL_MONO  = 4;

        public const int FRAME_SEQUENCER_RATE = 512;
        public const int LENGTH_RATE          = 256;

        public const int SQUARE_1_SWEEP_PERIOD     = 0xFF10;
        public const int SQUARE_1_DUTY_LENGTH_LOAD = 0xFF11;
        public const int SQUARE_1_VOLUME_ENVELOPE  = 0xFF12;
        public const int SQUARE_1_FREQUENCY_LSB    = 0xFF13;
        public const int SQUARE_1_FREQUENCY_MSB    = 0xFF14;

        public const int SQUARE_2_DUTY_LENGTH_LOAD = 0xFF16;
        public const int SQUARE_2_VOLUME_ENVELOPE  = 0xFF17;
        public const int SQUARE_2_FREQUENCY_LSB    = 0xFF18;
        public const int SQUARE_2_FREQUENCY_MSB    = 0xFF19;

        public const int VIN_VOL_CONTROL = 0xFF24;
        public const int STEREO_SELECT   = 0xFF25;
        public const int SOUND_ENABLED   = 0xFF26;
    }
}
