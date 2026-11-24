namespace PhotosViewer.Core;

public static class PhotoRating
{
    public const int Rejected = -1;
    public const int Unrated = 0;
    public const int Highest = 5;

    public static bool IsValid(int rating) => rating is >= Rejected and <= Highest;
}
