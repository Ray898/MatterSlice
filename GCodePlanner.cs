/*
This file is part of MatterSlice. A commandline utility for
generating 3D printing GCode.

Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using MSClipperLib;
using System.Collections.Generic;

namespace MatterHackers.MatterSlice
{
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	public class Segment
	{
		public IntPoint Start;
		public IntPoint End;

		public Segment()
		{

		}

		public Segment(IntPoint start, IntPoint end)
		{
			this.Start = start;
			this.End = end;
		}

		public static List<Segment> ConvertToSegments(Polygons polygons, bool pathsAreClosed = true)
		{
			List<Segment> polySegments = new List<Segment>();
			foreach (var polygon in polygons)
			{
				polySegments.AddRange(ConvertToSegments(polygon, pathsAreClosed));
			}

			return polySegments;
		}

		public static List<Segment> ConvertToSegments(Polygon polygon, bool pathIsClosed = true)
		{
			List<Segment> polySegments = new List<Segment>(polygon.Count);
			int endIndex = pathIsClosed ? polygon.Count : polygon.Count - 1;
			for (int i = 0; i < endIndex; i++)
			{
				IntPoint point = polygon[i];
				IntPoint nextPoint = polygon[(i + 1) % polygon.Count];

				polySegments.Add(new Segment()
				{
					Start = point,
					End = nextPoint,
				});
			}

			return polySegments;
		}

		public long LengthSquared()
		{
			return (this.End - this.Start).LengthSquared();
		}

		public static bool operator ==(Segment p0, Segment p1)
		{
			return p0.Start == p1.Start && p0.End == p1.End;
		}

		public static bool operator !=(Segment p0, Segment p1)
		{
			return p0.Start != p1.Start || p0.End != p1.End;
		}

		public static List<Segment> ConvertPathToSegments(IList<IntPoint> path, long zHeight, bool pathIsClosed = true)
		{
			List<Segment> polySegments = new List<Segment>(path.Count);
			int endIndex = pathIsClosed ? path.Count : path.Count - 1;
			for (int i = 0; i < endIndex; i++)
			{
				IntPoint point = new IntPoint(path[i])
				{
					Z = zHeight
				};
				int nextIndex = (i + 1) % path.Count;
				IntPoint nextPoint = new IntPoint(path[nextIndex])
				{
					Z = zHeight
				};

				polySegments.Add(new Segment()
				{
					Start = point,
					End = nextPoint,
				});
			}

			return polySegments;
		}

		public List<Segment> GetSplitSegmentForVertecies(Polygon splitPoints, long maxDistance)
		{
			IntPoint start2D = new IntPoint(Start)
			{
				Z = 0
			};
			IntPoint end2D = new IntPoint(End)
			{
				Z = 0
			};

			SortedList<long, IntPoint> requiredSplits2D = new SortedList<long, IntPoint>();

			// get some data we will need for the operations
			IntPoint direction = (end2D - start2D);
			long length = direction.Length();
			long lengthSquared = length * length;
			IntPoint rightDirection = direction.GetPerpendicularRight();
			long maxDistanceNormalized = maxDistance * length;

			// for every vertex
			for (int splintIndex = 0; splintIndex < splitPoints.Count; splintIndex++)
			{
				IntPoint vertex = new IntPoint(splitPoints[splintIndex]) { Z = 0 } - start2D;
				// if the vertex is close enough to the segment
				long dotProduct = rightDirection.Dot(vertex);
				if (Math.Abs(dotProduct) < maxDistanceNormalized)
				{
					long dotProduct2 = direction.Dot(vertex);
					if (dotProduct2 > 0 && dotProduct2 < lengthSquared)
					{
						long distance = dotProduct2 / length;
						// don't add if there is already a point at this position
						if (!requiredSplits2D.ContainsKey(distance))
						{
							// we are close enough to the line split it
							requiredSplits2D.Add(distance, start2D + direction.Normal(distance));
						}
					}
				}
			}

			if (requiredSplits2D.Count > 0)
			{
				// add in the start and end
				if (!requiredSplits2D.ContainsKey(0))
				{
					requiredSplits2D.Add(0, start2D);
				}
				if (!requiredSplits2D.ContainsKey(length))
				{
					requiredSplits2D.Add(length, end2D);
				}
				// convert to a segment list
				List<Segment> newSegments = Segment.ConvertPathToSegments(requiredSplits2D.Values, Start.Z, false);
				// return them;
				return newSegments;
			}

			return null;
		}
	}

	//The GCodePlanner class stores multiple moves that are planned.
	// It facilitates the avoidCrossingPerimeters to keep the head inside the print.
	// It also keeps track of the print time estimate for this planning so speed adjustments can be made for the minimum-layer-time.
	public class GCodePlanner
	{
		private bool alwaysRetract;

		private int currentExtruderIndex;

		private double extraTime;

		private int extrudeSpeedFactor;

		private bool forceRetraction;

		private GCodeExport gcodeExport = new GCodeExport();

		public long CurrentZ { get { return gcodeExport.CurrentZ; } }

		public IntPoint LastPosition
		{
			get; private set;
		}

		private AvoidCrossingPerimeters outerPerimetersToAvoidCrossing;

		private List<GCodePath> paths = new List<GCodePath>();

		private int retractionMinimumDistance_um;

		private double totalPrintTime;

		private GCodePathConfig travelConfig;

		private int travelSpeedFactor;

		double perimeterStartEndOverlapRatio;
		private bool mergeOverlappingLines;

		public GCodePlanner(GCodeExport gcode, int travelSpeed, int retractionMinimumDistance_um, double perimeterStartEndOverlap = 0, bool mergeOverlappingLines = false)
		{
			this.mergeOverlappingLines = mergeOverlappingLines;
			this.gcodeExport = gcode;
			travelConfig = new GCodePathConfig("travelConfig");
			travelConfig.SetData(travelSpeed, 0, "travel");

			LastPosition = gcode.GetPositionXY();
			outerPerimetersToAvoidCrossing = null;
			extrudeSpeedFactor = 100;
			travelSpeedFactor = 100;
			extraTime = 0.0;
			totalPrintTime = 0.0;
			forceRetraction = false;
			alwaysRetract = false;
			currentExtruderIndex = gcode.GetExtruderIndex();
			this.retractionMinimumDistance_um = retractionMinimumDistance_um;

			this.perimeterStartEndOverlapRatio = Math.Max(0, Math.Min(1, perimeterStartEndOverlap));
		}

		public void ForceMinimumLayerTime(double minTime, int minimumPrintingSpeed)
		{
			IntPoint lastPosition = gcodeExport.GetPosition();
			double travelTime = 0.0;
			double extrudeTime = 0.0;
			for (int n = 0; n < paths.Count; n++)
			{
				GCodePath path = paths[n];
				for (int pointIndex = 0; pointIndex < path.points.Count; pointIndex++)
				{
					IntPoint currentPosition = path.points[pointIndex];
					double thisTime = (lastPosition - currentPosition).LengthMm() / (double)(path.config.speed);
					if (path.config.lineWidth_um != 0)
					{
						extrudeTime += thisTime;
					}
					else
					{
						travelTime += thisTime;
					}

					lastPosition = currentPosition;
				}
			}

			double totalTime = extrudeTime + travelTime;
			if (totalTime < minTime && extrudeTime > 0.0)
			{
				double minExtrudeTime = minTime - travelTime;
				if (minExtrudeTime < 1)
				{
					minExtrudeTime = 1;
				}

				double factor = extrudeTime / minExtrudeTime;
				for (int n = 0; n < paths.Count; n++)
				{
					GCodePath path = paths[n];
					if (path.config.lineWidth_um == 0)
					{
						continue;
					}

					int speed = (int)(path.config.speed * factor);
					if (speed < minimumPrintingSpeed)
					{
						factor = (double)(minimumPrintingSpeed) / (double)(path.config.speed);
					}
				}

				//Only slow down with the minimum time if that will be slower then a factor already set. First layer slowdown also sets the speed factor.
				if (factor * 100 < getExtrudeSpeedFactor())
				{
					SetExtrudeSpeedFactor((int)(factor * 100));
				}
				else
				{
					factor = getExtrudeSpeedFactor() / 100.0;
				}

				if (minTime - (extrudeTime / factor) - travelTime > 0.1)
				{
					//TODO: Use up this extra time (circle around the print?)
					this.extraTime = minTime - (extrudeTime / factor) - travelTime;
				}
				this.totalPrintTime = (extrudeTime / factor) + travelTime;
			}
			else
			{
				this.totalPrintTime = totalTime;
			}
		}

		public void ForceRetract()
		{
			forceRetraction = true;
		}

		public int GetExtruder()
		{
			return currentExtruderIndex;
		}

		public int getExtrudeSpeedFactor()
		{
			return this.extrudeSpeedFactor;
		}

		[Flags]
		enum Altered { remove = 1, merged = 2 };

		public static bool FindThinLines(Polygon polygon, long overlapMergeAmount_um, long minimumRequiredWidth_um, out Polygons onlyMergeLines, bool pathIsClosed = true)
		{
			return FindThinLines(new Polygons { polygon }, overlapMergeAmount_um, minimumRequiredWidth_um, out onlyMergeLines, pathIsClosed);
		}

		public static bool FindThinLines(Polygons polygons, long overlapMergeAmount_um, long minimumRequiredWidth_um, out Polygons onlyMergeLines, bool pathIsClosed = true)
		{
			bool pathHasMergeLines = false;

			polygons = MakeCloseSegmentsMergable(polygons, overlapMergeAmount_um, pathIsClosed);

			// make a copy that has every point duplicated (so that we have them as segments).
			List<Segment> polySegments = Segment.ConvertToSegments(polygons);

			Altered[] markedAltered = new Altered[polySegments.Count];

			int segmentCount = polySegments.Count;
			// now walk every segment and check if there is another segment that is similar enough to merge them together
			for (int firstSegmentIndex = 0; firstSegmentIndex < segmentCount; firstSegmentIndex++)
			{
				for (int checkSegmentIndex = firstSegmentIndex + 1; checkSegmentIndex < segmentCount; checkSegmentIndex++)
				{
					// The first point of start and the last point of check (the path will be coming back on itself).
					long startDelta = (polySegments[firstSegmentIndex].Start - polySegments[checkSegmentIndex].End).Length();
					// if the segments are similar enough
					if (startDelta < overlapMergeAmount_um)
					{
						// The last point of start and the first point of check (the path will be coming back on itself).
						long endDelta = (polySegments[firstSegmentIndex].End - polySegments[checkSegmentIndex].Start).Length();
						if (endDelta < overlapMergeAmount_um)
						{
							// move the first segments points to the average of the merge positions
							long startEndWidth = Math.Abs((polySegments[firstSegmentIndex].Start - polySegments[checkSegmentIndex].End).Length());
							long endStartWidth = Math.Abs((polySegments[firstSegmentIndex].End - polySegments[checkSegmentIndex].Start).Length());
							long width = Math.Min(startEndWidth, endStartWidth);

							if (width > minimumRequiredWidth_um)
							{
								// We need to check if the new start position is on the inside of the curve. We can only add thin lines on the insides of our exisiting curves.
								IntPoint newStartPosition = (polySegments[firstSegmentIndex].Start + polySegments[checkSegmentIndex].End) / 2; // the start;
								IntPoint newStartDirection = newStartPosition - polySegments[firstSegmentIndex].Start;
								IntPoint normalLeft = (polySegments[firstSegmentIndex].End - polySegments[firstSegmentIndex].Start).GetPerpendicularLeft();
								long dotProduct = normalLeft.Dot(newStartDirection);
								if (dotProduct > 0)
								{
									pathHasMergeLines = true;

									polySegments[firstSegmentIndex].Start = newStartPosition;
									polySegments[firstSegmentIndex].Start.Width = width;
									polySegments[firstSegmentIndex].End = (polySegments[firstSegmentIndex].End + polySegments[checkSegmentIndex].Start) / 2; // the end
									polySegments[firstSegmentIndex].End.Width = width;

									markedAltered[firstSegmentIndex] = Altered.merged;
									// mark this segment for removal
									markedAltered[checkSegmentIndex] = Altered.remove;
									// We only expect to find one match for each segment, so move on to the next segment
									break;
								}
							}
						}
					}
				}
			}

			// remove the marked segments
			for (int segmentIndex = segmentCount - 1; segmentIndex >= 0; segmentIndex--)
			{
				// remove every segment that has not been merged
				if (markedAltered[segmentIndex] != Altered.merged)
				{
					polySegments.RemoveAt(segmentIndex);
				}
			}

			// go through the polySegments and create a new polygon for every connected set of segments
			onlyMergeLines = new Polygons();
			Polygon currentPolygon = new Polygon();
			onlyMergeLines.Add(currentPolygon);
			// put in the first point
			for (int segmentIndex = 0; segmentIndex < polySegments.Count; segmentIndex++)
			{
				// add the start point
				currentPolygon.Add(polySegments[segmentIndex].Start);

				// if the next segment is not connected to this one
				if (segmentIndex < polySegments.Count - 1
					&& polySegments[segmentIndex].End != polySegments[segmentIndex + 1].Start)
				{
					// add the end point
					currentPolygon.Add(polySegments[segmentIndex].End);

					// create a new polygon
					currentPolygon = new Polygon();
					onlyMergeLines.Add(currentPolygon);
				}
			}

			// add the end point
			if (polySegments.Count > 0)
			{
				currentPolygon.Add(polySegments[polySegments.Count - 1].End);
			}

			long cleanDistance_um = overlapMergeAmount_um / 40;
			//Clipper.CleanPolygons(onlyMergeLines, cleanDistance_um);

			return pathHasMergeLines;
		}


		public static bool MergePerimeterOverlaps(Polygon perimeter, long overlapMergeAmount_um, out Polygons separatedPolygons, bool pathIsClosed = true)
		{
			separatedPolygons = new Polygons();

			long cleanDistance_um = overlapMergeAmount_um / 40;

			Polygons cleanedPolygs = Clipper.CleanPolygons(new Polygons() { perimeter }, cleanDistance_um);
			perimeter = cleanedPolygs[0];

			if (perimeter.Count == 0)
			{
				return false;
			}
			bool pathWasOptomized = false;

			for(int i=0; i<perimeter.Count; i++)
			{
				perimeter[i] = new IntPoint(perimeter[i])
				{
					Width = overlapMergeAmount_um
				};
			}

			perimeter = MakeCloseSegmentsMergable(perimeter, overlapMergeAmount_um, pathIsClosed);

			// make a copy that has every point duplicated (so that we have them as segments).
			List<Segment> polySegments = Segment.ConvertToSegments(perimeter, pathIsClosed);

			Altered[] markedAltered = new Altered[polySegments.Count];

			int segmentCount = polySegments.Count;
			// now walk every segment and check if there is another segment that is similar enough to merge them together
			for (int firstSegmentIndex = 0; firstSegmentIndex < segmentCount; firstSegmentIndex++)
			{
				//polySegments[firstSegmentIndex].Start.Width = overlapMergeAmount_um;
				//polySegments[firstSegmentIndex].End.Width = overlapMergeAmount_um;

				for (int checkSegmentIndex = firstSegmentIndex + 1; checkSegmentIndex < segmentCount; checkSegmentIndex++)
				{
					// The first point of start and the last point of check (the path will be coming back on itself).
					long startDelta = (polySegments[firstSegmentIndex].Start - polySegments[checkSegmentIndex].End).Length();
					// if the segments are similar enough
					if (startDelta < overlapMergeAmount_um)
					{
						// The last point of start and the first point of check (the path will be coming back on itself).
						long endDelta = (polySegments[firstSegmentIndex].End - polySegments[checkSegmentIndex].Start).Length();
						if (endDelta < overlapMergeAmount_um)
						{
							// only considre the merge if the directions of the lines are towards eachother
							var firstSegmentDirection = polySegments[firstSegmentIndex].End - polySegments[firstSegmentIndex].Start;
							var checkSegmentDirection = polySegments[checkSegmentIndex].End - polySegments[checkSegmentIndex].Start;
							if (firstSegmentDirection.Dot(checkSegmentDirection) > 0)
							{
								continue;
							}
							pathWasOptomized = true;
							// move the first segments points to the average of the merge positions
							long startEndWidth = Math.Abs((polySegments[firstSegmentIndex].Start - polySegments[checkSegmentIndex].End).Length());
							long endStartWidth = Math.Abs((polySegments[firstSegmentIndex].End - polySegments[checkSegmentIndex].Start).Length());
							long width = Math.Min(startEndWidth, endStartWidth) + overlapMergeAmount_um;
							polySegments[firstSegmentIndex].Start = (polySegments[firstSegmentIndex].Start + polySegments[checkSegmentIndex].End) / 2; // the start
							polySegments[firstSegmentIndex].Start.Width = width;
							polySegments[firstSegmentIndex].End = (polySegments[firstSegmentIndex].End + polySegments[checkSegmentIndex].Start) / 2; // the end
							polySegments[firstSegmentIndex].End.Width = width;

							markedAltered[firstSegmentIndex] = Altered.merged;
							// mark this segment for removal
							markedAltered[checkSegmentIndex] = Altered.remove;
							// We only expect to find one match for each segment, so move on to the next segment
							break;
						}
					}
				}
			}

			// remove the marked segments
			for (int segmentIndex = segmentCount - 1; segmentIndex >= 0; segmentIndex--)
			{
				if (markedAltered[segmentIndex] == Altered.remove)
				{
					polySegments.RemoveAt(segmentIndex);
				}
			}

			// go through the polySegments and create a new polygon for every connected set of segments
			Polygon currentPolygon = new Polygon();
			separatedPolygons.Add(currentPolygon);
			// put in the first point
			for (int segmentIndex = 0; segmentIndex < polySegments.Count; segmentIndex++)
			{
				// add the start point
				currentPolygon.Add(polySegments[segmentIndex].Start);

				// if the next segment is not connected to this one
				if (segmentIndex < polySegments.Count - 1
					&& polySegments[segmentIndex].End != polySegments[segmentIndex + 1].Start)
				{
					// add the end point
					currentPolygon.Add(polySegments[segmentIndex].End);

					// create a new polygon
					currentPolygon = new Polygon();
					separatedPolygons.Add(currentPolygon);
				}
			}

			// add the end point
			currentPolygon.Add(polySegments[polySegments.Count - 1].End);

			return pathWasOptomized;
		}

		public int getTravelSpeedFactor()
		{
			return this.travelSpeedFactor;
		}

		public void MoveInsideTheOuterPerimeter(int distance)
		{
			if (outerPerimetersToAvoidCrossing == null || outerPerimetersToAvoidCrossing.PointIsInsideBoundary(LastPosition))
			{
				return;
			}

			IntPoint p = LastPosition;
			if (outerPerimetersToAvoidCrossing.MovePointInsideBoundary(ref p, distance))
			{
				//Move inside again, so we move out of tight 90deg corners
				outerPerimetersToAvoidCrossing.MovePointInsideBoundary(ref p, distance);
				if (outerPerimetersToAvoidCrossing.PointIsInsideBoundary(p))
				{
					QueueTravel(p);
					//Make sure the that any retraction happens after this move, not before it by starting a new move path.
					ForceNewPathStart();
				}
			}
		}

		public void SetAlwaysRetract(bool alwaysRetract)
		{
			this.alwaysRetract = alwaysRetract;
		}

		public bool SetExtruder(int extruder)
		{
			if (extruder == currentExtruderIndex)
			{
				return false;
			}

			currentExtruderIndex = extruder;
			return true;
		}

		public void SetExtrudeSpeedFactor(int speedFactor)
		{
			if (speedFactor < 1) speedFactor = 1;
			this.extrudeSpeedFactor = speedFactor;
		}

		public void SetOuterPerimetersToAvoidCrossing(Polygons polygons)
		{
			if (polygons != null)
			{
				outerPerimetersToAvoidCrossing = new AvoidCrossingPerimeters(polygons);
			}
			else
			{
				outerPerimetersToAvoidCrossing = null;
			}
		}

		public void SetTravelSpeedFactor(int speedFactor)
		{
			if (speedFactor < 1) speedFactor = 1;
			this.travelSpeedFactor = speedFactor;
		}

		public void QueueExtrusionMove(IntPoint destination, GCodePathConfig config)
		{
			GetLatestPathWithConfig(config).points.Add(new IntPoint(destination, CurrentZ));
			LastPosition = destination;
		}

		public void WriteQueuedGCode(int layerThickness, int fanSpeedPercent = -1, int bridgeFanSpeedPercent = -1)
		{
			GCodePathConfig lastConfig = null;
			int extruderIndex = gcodeExport.GetExtruderIndex();

			for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
			{
				GCodePath path = paths[pathIndex];
				if (extruderIndex != path.extruderIndex)
				{
					extruderIndex = path.extruderIndex;
					gcodeExport.SwitchExtruder(extruderIndex);
				}
				else if (path.Retract)
				{
					gcodeExport.WriteRetraction();
				}
				if (path.config != travelConfig && lastConfig != path.config)
				{
					if (path.config.gcodeComment == "BRIDGE" && bridgeFanSpeedPercent != -1)
					{
						gcodeExport.WriteFanCommand(bridgeFanSpeedPercent);
					}
					else if (lastConfig?.gcodeComment == "BRIDGE" && bridgeFanSpeedPercent != -1)
					{
						gcodeExport.WriteFanCommand(fanSpeedPercent);
					}

					gcodeExport.WriteComment("TYPE:{0}".FormatWith(path.config.gcodeComment));
					lastConfig = path.config;
				}

				double speed = path.config.speed;

				if (path.config.lineWidth_um != 0)
				{
					// Prevent cooling overrides from affecting bridge moves
					if (path.config.gcodeComment != "BRIDGE")
					{
						speed = speed * extrudeSpeedFactor / 100;
					}
				}
				else
				{
					speed = speed * travelSpeedFactor / 100;
				}

				if (path.points.Count == 1
					&& path.config != travelConfig
					&& (gcodeExport.GetPositionXY() - path.points[0]).ShorterThen(path.config.lineWidth_um * 2))
				{
					//Check for lots of small moves and combine them into one large line
					IntPoint nextPosition = path.points[0];
					int i = pathIndex + 1;
					while (i < paths.Count && paths[i].points.Count == 1 && (nextPosition - paths[i].points[0]).ShorterThen(path.config.lineWidth_um * 2))
					{
						nextPosition = paths[i].points[0];
						i++;
					}
					if (paths[i - 1].config == travelConfig)
					{
						i--;
					}

					if (i > pathIndex + 2)
					{
						nextPosition = gcodeExport.GetPosition();
						for (int x = pathIndex; x < i - 1; x += 2)
						{
							long oldLen = (nextPosition - paths[x].points[0]).Length();
							IntPoint newPoint = (paths[x].points[0] + paths[x + 1].points[0]) / 2;
							long newLen = (gcodeExport.GetPosition() - newPoint).Length();
							if (newLen > 0)
							{
								gcodeExport.WriteMove(newPoint, speed, (int)(path.config.lineWidth_um * oldLen / newLen));
							}

							nextPosition = paths[x + 1].points[0];
						}

						long lineWidth_um = path.config.lineWidth_um;
						if (paths[i - 1].points[0].Width != 0)
						{
							lineWidth_um = paths[i - 1].points[0].Width;
						}

						gcodeExport.WriteMove(paths[i - 1].points[0], speed, lineWidth_um);
						pathIndex = i - 1;
						continue;
					}
				}


				bool spiralize = path.config.spiralize;
				if (spiralize)
				{
					//Check if we are the last spiralize path in the list, if not, do not spiralize.
					for (int m = pathIndex + 1; m < paths.Count; m++)
					{
						if (paths[m].config.spiralize)
						{
							spiralize = false;
						}
					}
				}

				if (spiralize) // if we are still in spiralize mode
				{
					//If we need to spiralize then raise the head slowly by 1 layer as this path progresses.
					double totalLength = 0;
					long z = gcodeExport.GetPositionZ();
					IntPoint currentPosition = gcodeExport.GetPositionXY();
					for (int pointIndex = 0; pointIndex < path.points.Count; pointIndex++)
					{
						IntPoint nextPosition = path.points[pointIndex];
						totalLength += (currentPosition - nextPosition).LengthMm();
						currentPosition = nextPosition;
					}

					double length = 0.0;
					currentPosition = gcodeExport.GetPositionXY();
					for (int i = 0; i < path.points.Count; i++)
					{
						IntPoint nextPosition = path.points[i];
						length += (currentPosition - nextPosition).LengthMm();
						currentPosition = nextPosition;
						IntPoint nextExtrusion = path.points[i];
						nextExtrusion.Z = (int)(z + layerThickness * length / totalLength + .5);
						gcodeExport.WriteMove(nextExtrusion, speed, path.config.lineWidth_um);
					}
				}
				else
				{
					// This is test code to remove double drawn small perimeter lines.
					Polygons pathsWithOverlapsRemoved = null;
					bool pathHadOverlaps = false;
					bool pathIsClosed = true;
					if (mergeOverlappingLines
						&& ( path.config.gcodeComment == "WALL-OUTER" || path.config.gcodeComment == "WALL-INNER"))
					{
						//string perimeterString = Newtonsoft.Json.JsonConvert.SerializeObject(path);
						if (perimeterStartEndOverlapRatio < 1)
						{
							path = TrimPerimeter(path, perimeterStartEndOverlapRatio);
							//string trimmedString = Newtonsoft.Json.JsonConvert.SerializeObject(path);
							// it was closed but now it isn't
							pathIsClosed = false;
						}

						if (path.config.lineWidth_um > 0
							&& path.points.Count > 2)
						{
							// have to add in the position we are currently at
							path.points.Insert(0, gcodeExport.GetPosition());
							//string openPerimeterString = Newtonsoft.Json.JsonConvert.SerializeObject(path);
							pathHadOverlaps = MergePerimeterOverlaps(path.points, path.config.lineWidth_um, out pathsWithOverlapsRemoved, pathIsClosed)
								&& pathsWithOverlapsRemoved.Count > 0;
							//string trimmedString = Newtonsoft.Json.JsonConvert.SerializeObject(pathsWithOverlapsRemoved);
						}
					}

					if (pathHadOverlaps)
					{
						for (int polygonIndex = 0; polygonIndex < pathsWithOverlapsRemoved.Count; polygonIndex++)
						{
							Polygon polygon = pathsWithOverlapsRemoved[polygonIndex];

							if (polygon.Count == 2)
							{
								// make sure the path is ordered with the first point the closest to where we are now
								IntPoint currentPosition = gcodeExport.GetPosition();
								// if the second point is closer swap them
								if ((polygon[1] - currentPosition).LengthSquared() < (polygon[0] - currentPosition).LengthSquared())
								{
									// swap them
									IntPoint temp = polygon[0];
									polygon[0] = polygon[1];
									polygon[1] = temp;
								}
							}

							// move to the start of this polygon
							gcodeExport.WriteMove(polygon[0], travelConfig.speed, 0);

							// write all the data for the polygon
							for (int pointIndex = 1; pointIndex < polygon.Count; pointIndex++)
							{
								gcodeExport.WriteMove(polygon[pointIndex], speed, polygon[pointIndex-1].Width);
							}
						}
					}
					else
					{
						int outputCount = path.points.Count;
						for (int i = 0; i < outputCount; i++)
						{
							long lineWidth_um = path.config.lineWidth_um;
							if (path.points[i].Width != 0)
							{
								lineWidth_um = path.points[i].Width;
							}

							gcodeExport.WriteMove(path.points[i], speed, lineWidth_um);
						}
					}
				}
			}

			gcodeExport.UpdateTotalPrintTime();
		}

		public void QueuePolygons(Polygons polygons, GCodePathConfig config)
		{
			foreach (var polygon in polygons)
			{
				QueuePolygon(polygon, 0, config);
			}
		}

		public void QueuePolygon(Polygon polygon, int startIndex, GCodePathConfig config)
		{
			IntPoint currentPosition = polygon[startIndex];

			if (!config.spiralize
				&& (LastPosition.X != currentPosition.X
				|| LastPosition.Y != currentPosition.Y))
			{
				QueueTravel(currentPosition);
			}

			if (config.closedLoop)
			{
				for (int positionIndex = 1; positionIndex < polygon.Count; positionIndex++)
				{
					IntPoint destination = polygon[(startIndex + positionIndex) % polygon.Count];
					QueueExtrusionMove(destination, config);
					currentPosition = destination;
				}

				// We need to actually close the polygon so go back to the first point
				if (polygon.Count > 2)
				{
					QueueExtrusionMove(polygon[startIndex], config);
				}
			}
			else // we are not closed
			{
				if (startIndex == 0)
				{
					for (int positionIndex = 1; positionIndex < polygon.Count; positionIndex++)
					{
						IntPoint destination = polygon[positionIndex];
						QueueExtrusionMove(destination, config);
						currentPosition = destination;
					}
				}
				else
				{
					for (int positionIndex = polygon.Count - 1; positionIndex >= 1; positionIndex--)
					{
						IntPoint destination = polygon[(startIndex + positionIndex) % polygon.Count];
						QueueExtrusionMove(destination, config);
						currentPosition = destination;
					}
				}
			}
		}

		public void QueuePolygonsByOptimizer(Polygons polygons, GCodePathConfig config)
		{
			if(polygons.Count == 0)
			{
				return;
			}

			PathOrderOptimizer orderOptimizer = new PathOrderOptimizer(LastPosition);
			orderOptimizer.AddPolygons(polygons);

			orderOptimizer.Optimize(config);

			for (int i = 0; i < orderOptimizer.bestIslandOrderIndex.Count; i++)
			{
				int polygonIndex = orderOptimizer.bestIslandOrderIndex[i];
				QueuePolygon(polygons[polygonIndex], orderOptimizer.startIndexInPolygon[polygonIndex], config);
			}
		}

		public void QueueTravel(IntPoint positionToMoveTo)
		{
			GCodePath path = GetLatestPathWithConfig(travelConfig);

			if (forceRetraction)
			{
				path.Retract = true;
				forceRetraction = false;
			}
			else if (outerPerimetersToAvoidCrossing != null)
			{
				Polygon pointList = new Polygon();
				if (outerPerimetersToAvoidCrossing.CreatePathInsideBoundary(LastPosition, positionToMoveTo, pointList))
				{
					long lineLength_um = 0;
					// we can stay inside so move within the boundary
					for (int pointIndex = 0; pointIndex < pointList.Count; pointIndex++)
					{
						path.points.Add(new IntPoint(pointList[pointIndex], CurrentZ)
						{
							Width = 0
						});
						if (pointIndex > 0)
						{
							lineLength_um += (pointList[pointIndex] - pointList[pointIndex - 1]).Length();
						}
					}

					// If the internal move is very long (20 mm), do a retraction anyway
					if (lineLength_um > retractionMinimumDistance_um)
					{
						path.Retract = true;
					}
				}
				else
				{
					if ((LastPosition - positionToMoveTo).LongerThen(retractionMinimumDistance_um))
					{
						// We are moving relatively far and are going to cross a boundary so do a retraction.
						path.Retract = true;
					}
				}
			}
			else if (alwaysRetract)
			{
				if ((LastPosition - positionToMoveTo).LongerThen(retractionMinimumDistance_um))
				{
					path.Retract = true;
				}
			}

			path.points.Add(new IntPoint(positionToMoveTo, CurrentZ)
			{
				Width = 0,
			});
			LastPosition = positionToMoveTo;
		}

		public static GCodePath TrimPerimeter(GCodePath inPath, double perimeterStartEndOverlapRatio)
		{
			GCodePath path = new GCodePath(inPath);
			long currentDistance = 0;
			long targetDistance = (long)(path.config.lineWidth_um * (1 - perimeterStartEndOverlapRatio));

			if (path.points.Count > 1)
			{
				for (int pointIndex = path.points.Count - 1; pointIndex > 0; pointIndex--)
				{
					// Calculate distance between 2 points
					currentDistance = (path.points[pointIndex] - path.points[pointIndex - 1]).Length();

					// If distance exceeds clip distance:
					//  - Sets the new last path point
					if (currentDistance > targetDistance)
					{
						long newDistance = currentDistance - targetDistance;
						if (targetDistance > 50) // Don't clip segments less than 50 um. We get too much truncation error.
						{
							IntPoint dir = (path.points[pointIndex] - path.points[pointIndex - 1]) * newDistance / currentDistance;

							IntPoint clippedEndpoint = path.points[pointIndex - 1] + dir;

							path.points[pointIndex] = clippedEndpoint;
						}
						break;
					}
					else if (currentDistance == targetDistance)
					{
						// Pops off last point because it is at the limit distance
						path.points.RemoveAt(path.points.Count - 1);
						break;
					}
					else
					{
						// Pops last point and reduces distance remaining to target
						targetDistance -= currentDistance;
						path.points.RemoveAt(path.points.Count - 1);
					}
				}
			}

			return path;
		}

		private void ForceNewPathStart()
		{
			if (paths.Count > 0)
			{
				paths[paths.Count - 1].done = true;
			}
		}

		private GCodePath GetLatestPathWithConfig(GCodePathConfig config)
		{
			if (paths.Count > 0
				&& paths[paths.Count - 1].config == config
				&& !paths[paths.Count - 1].done)
			{
				return paths[paths.Count - 1];
			}

			paths.Add(new GCodePath());
			GCodePath ret = paths[paths.Count - 1];
			ret.Retract = false;
			ret.config = config;
			ret.extruderIndex = currentExtruderIndex;
			ret.done = false;
			return ret;
		}

		public static Polygons MakeCloseSegmentsMergable(Polygons polygonsToSplit, long distanceNeedingAdd, bool pathsAreClosed = true)
		{
			Polygons splitPolygons = new Polygons();
			foreach(var polygonToSplit in polygonsToSplit)
			{
				Polygon accumulatedSplits = polygonToSplit;
				foreach (var pointsToSplitOn in polygonsToSplit)
				{
					accumulatedSplits = MakeCloseSegmentsMergable(accumulatedSplits, pointsToSplitOn, distanceNeedingAdd, pathsAreClosed);
				}
				splitPolygons.Add(accumulatedSplits);
			}

			return splitPolygons;
		}

		public static Polygon MakeCloseSegmentsMergable(Polygon polygonToSplit, long distanceNeedingAdd, bool pathIsClosed = true)
		{
			return MakeCloseSegmentsMergable(polygonToSplit, polygonToSplit, distanceNeedingAdd, pathIsClosed);
		}

		public static Polygon MakeCloseSegmentsMergable(Polygon polygonToSplit, Polygon pointsToSplitOn, long distanceNeedingAdd, bool pathIsClosed = true)
		{
			List<Segment> segments = Segment.ConvertToSegments(polygonToSplit, pathIsClosed);

			// for every segment
			for (int segmentIndex = segments.Count - 1; segmentIndex >= 0; segmentIndex--)
			{
				List<Segment> newSegments = segments[segmentIndex].GetSplitSegmentForVertecies(pointsToSplitOn, distanceNeedingAdd);
				if (newSegments?.Count > 0)
				{
					// remove the old segment
					segments.RemoveAt(segmentIndex);
					// add the new ones
					segments.InsertRange(segmentIndex, newSegments);
				}
			}

			Polygon segmentedPolygon = new Polygon(segments.Count);

			foreach (var segment in segments)
			{
				segmentedPolygon.Add(segment.Start);
			}

			if (!pathIsClosed)
			{
				// add the last point
				segmentedPolygon.Add(segments[segments.Count - 1].End);
			}

			return segmentedPolygon;
		}
	}

	public class GCodePath
	{
		public GCodePathConfig config;
		/// <summary>
		/// Path is finished, no more moves should be added, and a new path should be started instead of any appending done to this one.
		/// </summary>
		internal bool done;
		internal int extruderIndex;
		public Polygon points = new Polygon();

		internal bool Retract { get; set; }

		public GCodePath()
		{
		}

		public GCodePath(GCodePath copyPath)
		{
			this.config = copyPath.config;
			this.done = copyPath.done;
			this.extruderIndex = copyPath.extruderIndex;
			this.Retract = copyPath.Retract;
			this.points = new Polygon(copyPath.points);
		}

		public long Length(bool pathIsClosed)
		{
			long totalLength = 0;
			for (int pointIndex = 0; pointIndex < points.Count - 1; pointIndex++)
			{
				// Calculate distance between 2 points
				totalLength += (points[pointIndex] - points[pointIndex + 1]).Length();
			}

			if (pathIsClosed)
			{
				// add in the move back to the start
				totalLength += (points[points.Count - 1] - points[0]).Length();
			}

			return totalLength;
		}
	}
}
