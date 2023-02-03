namespace CefCaptchaResolver
{
    public struct Coordinates
    {
        public int X { get; set; }
        public int Y { get; set; }

        public Coordinates(int x, int y)
        {
            X = x;
            Y = y;
        }

        public Coordinates(Coordinates coords)
        {
            X = coords.X;
            Y = coords.Y;
        }

        public void Change(int dX, int dY)
        {
            X += dX;
            Y += dY;
        }

        public override string ToString()
        {
            return $"x:{X} y:{Y}";
        }
    }
}
