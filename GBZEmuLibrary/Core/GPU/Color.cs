namespace GBZEmuLibrary
{
    public struct Color
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        internal int Index { get; set; }

        public Color(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;

            Index = 0;
        }

        public Color(Color color)
        {
            R = color.R;
            G = color.G;
            B = color.B;

            Index = color.Index;
        }
    }
}
