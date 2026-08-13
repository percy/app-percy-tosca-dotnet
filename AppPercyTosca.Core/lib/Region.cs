namespace AppPercyTosca.Core
{
    /// A rectangle in device pixels, used for the custom ignore/consider regions a Tosca module
    /// can declare as "top,bottom,left,right".
    public class Region
    {
        private int _top;
        private int _bottom;
        private int _left;
        private int _right;

        public Region(int top, int bottom, int left, int right)
        {
            if (top < 0 || bottom < 0 || left < 0 || right < 0)
                throw new ArgumentException("Only Positive integer is allowed!");
            _top = top;
            _bottom = bottom;
            _left = left;
            _right = right;
        }

        public int Top
        {
            get => _top;
            set
            {
                if (value < 0) throw new ArgumentException("Only Positive integer is allowed!");
                _top = value;
            }
        }

        public int Bottom
        {
            get => _bottom;
            set
            {
                if (value < 0) throw new ArgumentException("Only Positive integer is allowed!");
                _bottom = value;
            }
        }

        public int Left
        {
            get => _left;
            set
            {
                if (value < 0) throw new ArgumentException("Only Positive integer is allowed!");
                _left = value;
            }
        }

        public int Right
        {
            get => _right;
            set
            {
                if (value < 0) throw new ArgumentException("Only Positive integer is allowed!");
                _right = value;
            }
        }

        /// True when the region is non-degenerate and fits inside a screen of the given size.
        public bool IsValid(int height, int width)
        {
            if (_top >= _bottom || _left >= _right) return false;
            if (_top >= height || _bottom > height || _left >= width || _right > width) return false;
            return true;
        }
    }

    /// Named alias kept so callers can express intent at the call site.
    public class IgnoreRegion : Region
    {
        public IgnoreRegion(int top, int bottom, int left, int right) : base(top, bottom, left, right)
        {
        }
    }
}
