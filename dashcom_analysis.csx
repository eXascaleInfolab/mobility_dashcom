#!/usr/bin/env dotnet-script
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

// Read file name from CLI arguments
string filename = Args.Count > 0 ? Args[0] : "./recording_2022-05-30.txt";

if (!File.Exists(filename))
{
    Console.WriteLine($"Provided file {filename} cannot be found. Please verify the file's location.");
    return;
}

// declare optional room object
Room? room = null;

if (Args.Count > 1)
{
    string roomFileName = Args[1];

    if (File.Exists(roomFileName))
    {
        (double, double)[] polygon = EnumerateAllLines(roomFileName)
            .Select(line => line.Split(',')) // transform into string[]
            .Select(point => (Double.Parse(point[0]), Double.Parse(point[1]))) // parse coordinates
            .ToArray();
        
        if (polygon.Length < 3)
        {
            Console.WriteLine("Provided room file is malformed - the room polygon has to be defined by at least 3 points.");
            return;
        }
        
        room = new (polygon);
    }
    else
    {
        Console.WriteLine($"Provided room file {roomFileName} cannot be found. Proceeding using base coordinates.");
    }
}

// Define locations where to store the results
(string baseName, string baseExt) = SplitFileExtention(filename);
if (String.IsNullOrEmpty(baseExt)) baseExt = "txt";

string fileRecords = baseName + ".records." + baseExt;
string fileRelRecords = baseName + ".relative." + baseExt;

//
// Step 1 - Tidy up raw records
//

// Load the file line by line and transform each line into an object
// Since file doesn't have headers, the indices of relevant items are hardcoded
List<Record> series = EnumerateAllLines(filename)
    .Select(line => line.Split(',')) // transform into string[]
    .Select(cells => new Record() // create a Record object
    {
        latitude = Double.Parse(cells[3].Substring(cells[3].IndexOf(':') + 1)),
        longitude = Double.Parse(cells[4]),
        timestamp = DateTime.Parse(cells[9])
    })
    .Distinct() // remove duplicates
    .ToList();

Console.WriteLine($"Series loaded: {series.Count} distinct entries.");

// Set up "base" coordinates
// NB: Can be set to anything, e.g. center point of the room
// NB: For demo purposes of this script we use the first entry in time series
(double baseLat, double baseLong) = (series.First().latitude, series.First().longitude);
// Define a function to measure geo distance to base location
double DistanceToBase(Record record) => GeoDistance(record.latitude, record.longitude, baseLat, baseLong);

// transform each entry into a string that contains a transformed coordinate that removes digits that are always the same
// NB: it is only used for the output here, all calculations have to use full coordinate values
IEnumerable<string> records;

// this adds distance to base point (in either case), but if the room is provided - flag [INSIDE] is determined by its box and not by distance to base
if (room != null)
{
    records = series.Select(r => $"{r.ToHumanString()} -- {DistanceToBase(r):F4} {r.Within(room)}");
}
else // fallback: no room is provided
{
    // Assuming the room is a perfect circle this is the diameter of the room
    const double RoomRadius = 5; // meters
    
    records = series.Select(r => $"{r.ToHumanString()} -- {DistanceToBase(r):F4} {r.Within(baseLat, baseLong, RoomRadius)}");
}

// output the records into the file
FileWriteAllLines(fileRecords, records);

Console.WriteLine($"Records written to {fileRecords}");

//
// Step 2 - Transform the raw records into relative records
//

List<RecordRelative> steps = series // take the list of records
    .Pairwise() // function transforms the list of 1,2,3,4... into pairs of (1,2) (2,3) (3,4) ...
    .Select((e, o) => new RecordRelative() // populate the relative record object
    {
        // e = earlier record, o = later record, i.e. for (2,3) e=2, o=3
        timestamp = o.timestamp,
        distanceToLast = o.DistanceTo(e),
        timeToLast = o.timestamp - e.timestamp,
    })
    .ToList();

var relRecords = steps // take the list of relative records
    // transforms each entry into a string with distance & time difference to the previous record + estimated speed
    // also adds "infodump" containing relevant info and/or data quality warnings
    .Select(r => $"{r.ToString()} {r.Infodump()}");

// output the records into the file
FileWriteAllLines(fileRelRecords, relRecords);

Console.WriteLine($"Relative records written to {fileRelRecords}");

return;//exit

// =======================================

//
// Data types
//

struct Record
{
    //fields
    public DateTime timestamp;
    public double latitude;
    public double longitude;

    //properties
    public double HumanLat { get => (latitude * 1000 - 46997) * 1; }
    public double HumanLong { get => (longitude * 100 - 746) * 1; }

    //functions
    public override string ToString() => $"{timestamp.Ticks / 10000}: @{latitude} / {longitude}";
    public string ToHumanString() => $"{timestamp.ToLongTimeString()}: @{HumanLat:F6}/{HumanLong:F6}";

    // this is used to determine the distance of a current record to a different set of coordinates
    public double DistanceTo(Record other) => GeoDistance(this.latitude, this.longitude, other.latitude, other.longitude);
    // this is used to determine is record's coordinates are within a radius of some base point
    public string Within(double baseLat, double baseLong, double radius) =>
        GeoDistance(this.latitude, this.longitude, baseLat, baseLong) < radius
        ? "[INSIDE:BASE]"
        : "";
    // this is used to determine is record's coordinates are within a polygon given by the room object
    public string Within(Room room) =>
        room.IsPointWithin(this.latitude, this.longitude)
        ? "[INSIDE:ROOM]"
        : "";
}

struct RecordRelative
{
    //fields
    public DateTime timestamp;
    public TimeSpan timeToLast;
    public double distanceToLast;

    //properties
    public double Speed => distanceToLast / timeToLast.TotalSeconds;

    //functions
    public override string ToString() => $"{timestamp.ToLongTimeString()}: moved {distanceToLast:F4} m\tin {timeToLast.TotalSeconds:00.00} sec\tat the speed of {Speed:F4} m/s";

    public string Infodump()
    {
        string info = "";
        if (timeToLast.TotalSeconds > 60.0) // nothing for 1 minute between the current and the last
        {
            info += $"[INFO:LONG]";
        }
        if (Speed > 2.5) // more than 2.5 m/s which is an upper limit of human walking speed
        {
            info += $"[WARN:SPEED]";
        }
        return info;
    }
}

class Room
{
    // fields
    private (double, double)[] polygon;
    
    public Room((double, double)[] polygon)
    {
        this.polygon = polygon;
    }
    
    // functions
    public bool IsPointWithin(double latitude, double longitude)
    {
        // Method: check if the point is always at the same side of the sides of the polygon
        // If the convex polygon is well-defined (points are always clock-wise or counter-clock-wise) the "same side" (left or right) is always inside the polygon
        int pos = 0, neg = 0;

        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] == (latitude, longitude)) return true;
            
            (double x1, double y1) = polygon[i]; //current
            (double x2, double y2) = polygon[(i + 1) % polygon.Length]; //next, on index overflow go to the first point

            double d = (latitude - x1)*(y2 - y1) - (longitude - y1)*(x2 - x1);

            if (d > 0) pos++;
            if (d < 0) neg++;
            
            if (pos > 0 && neg > 0) return false;
        }

        return true;
    }
}

//
// Geo
//
private const double EarthRadius = 6378137.0; // meters
private static double ToRadians(double distDeg) => distDeg * Math.PI / 180.0;
public static double GeoDistance(double lat1, double lng1, double lat2, double lng2)
{
    // standard formula used in: https://en.wikipedia.org/wiki/Geographical_distance#Spherical_Earth_projected_to_a_plane
    double dLat = ToRadians(lat2-lat1);
    double dLng = ToRadians(lng2-lng1);
    double a = Math.Sin(dLat/2) * Math.Sin(dLat/2) +
               Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
               Math.Sin(dLng/2) * Math.Sin(dLng/2);
    double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
    return EarthRadius * c;
}

//
// Tools
//

// this is mostly a collection of helper functions, specifically for IO and LINQ
// most are quite straightforward

public static IEnumerable<string> EnumerateAllLines(string file)
{
    using var sr = new StreamReader(new FileStream(file, FileMode.Open));
    while (!sr.EndOfStream)
    {
        string? line = sr.ReadLine();
        if (line != null) yield return line;
    }
}

public static void FileWriteAllLines(string file, IEnumerable<string> collection)
{
    if (File.Exists(file))
    {
        File.Delete(file);
    }

    using var writer = new StreamWriter(new FileStream(file, FileMode.OpenOrCreate));

    foreach (string chunk in collection)
    {
        writer.WriteLine(chunk);
    }
}

// "file.ext" -> ("file", "ext")
public static (string, string) SplitFileExtention(string fileName)
{
    // edge case
    if (!fileName.Contains('.'))
    {
        return (fileName, "");
    }

    int index = 0;
    int start = 0;
    while ((index = fileName.IndexOf('.', start)) != -1)
    {
        start = index + 1;
    }
    return (fileName.Substring(0, start - 1), fileName.Substring(start));
}

// transforms a list of 1,2,3,4,5... into (1,2),(2,3),(3,4)...
public static IEnumerable<(T, T)> Pairwise<T>(this IEnumerable<T> collection)
{
    using (IEnumerator<T> e = collection.GetEnumerator()) //unroll the collection manually with the IEnumerator
    {
        if (!e.MoveNext())
            yield break;

        T last = e.Current;
        while (e.MoveNext())
        {
            yield return (last, e.Current);
            last = e.Current;
        }
    }
}

// a tuple-select function, essentially a standard map of (o1, o2) -> o3 applied to a list
// but the lambda (variable func) it takes is defined to take two values as arguments and not a single "tuple", so the deconstruction of the tuple happens inside this function and not in the user code
// in other words lambda is func/2: (o1, o2) -> o3 and not func/1: ((o1, o2)) -> o3
public static IEnumerable<TRes> Select<TSrc1, TSrc2, TRes>(
            this IEnumerable<(TSrc1, TSrc2)> collection,
            Func<TSrc1, TSrc2, TRes> func)
{
    return collection.Select(tuple => func(tuple.Item1, tuple.Item2));
}
