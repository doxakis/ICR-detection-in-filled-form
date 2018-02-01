﻿using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ICRExtraction
{
	public class FormExtraction
	{
		public static void ExtractCharacters(string pathFile, string resultDir, bool removeEmptyBoxes = false)
		{
			int groupId = 0;
			
			try
			{
				var filename = Path.GetFileNameWithoutExtension(pathFile);
				var result = FormExtraction.ProcessImage(pathFile);
				if (result.ReturnCode != 0)
				{
					throw new Exception("ReturnCode: " + result.ReturnCode);
				}

				Console.WriteLine("Processing: " + Path.GetFileNameWithoutExtension(pathFile) + ", Duration: " + result.Duration);

				using (var image = new Mat(pathFile, ImreadModes.GrayScale))
				{
					Cv2.AdaptiveThreshold(image, image, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 9, 4);

					// TODO: we should not resize. (keep maximum quality)
					if (image.Width > 800)
					{
						var height = 800 * image.Height / image.Width;
						Cv2.Resize(image, image, new Size(800, height));
					}

					// You may want to use ".OrderBy(m => m.Min(x => x.TopLeft.Y)).Take(1)" to select the first box on top.
					foreach (var group in result.Boxes)
					{
						// Console.WriteLine("\nGroup #" + numGroup + " (" + group.Count + ")");

						groupId++;

						int characterNum = 1;
						foreach (var box in group)
						{
							// Console.WriteLine(box.TopLeft + " " + box.TopRight + "\n" + box.BottomLeft + " " + box.BottomRight + "\n");

							var xTopLeft = Math.Min(box.TopLeft.X, box.BottomLeft.X);
							var yTopLeft = Math.Min(box.TopLeft.Y, box.TopRight.Y);

							var xBottomRight = Math.Max(box.TopRight.X, box.BottomRight.X);
							var yBottomRight = Math.Max(box.BottomLeft.Y, box.BottomRight.Y);

							var estimatedWidth = xBottomRight - xTopLeft;
							var estimatedHeight = yBottomRight - yTopLeft;

							try
							{
								using (var subImg = new Mat(image, new Rect(xTopLeft, yTopLeft, estimatedWidth, estimatedHeight)))
								{
									MatOfByte3 mat3 = new MatOfByte3(subImg);
									MatIndexer<Vec3b> indexer = mat3.GetIndexer();

									int borderPixelX = 4;
									int borderPixelY = 4;
									var minY = Math.Min(borderPixelX, subImg.Height);
									var maxY = Math.Max(0, subImg.Height - borderPixelX);
									var minX = Math.Min(borderPixelX, subImg.Width);
									var maxX = Math.Max(0, subImg.Width - borderPixelY);

									var outputFilename = filename + "_g-" + groupId + "_n-" + characterNum;

									if (removeEmptyBoxes)
									{
										// Basic empty box detection.
										int whitePixelCounter = 0;
										int pixelCounter = 0;
										for (int y = minY; y <= maxY; y++)
										{
											for (int x = minX; x <= maxX; x++)
											{
												var pixel = indexer[y, x].Item0; // Grayscale only

												if (pixel == 255)
												{
													whitePixelCounter++;
												}
												pixelCounter++;
											}
										}
										mat3.Dispose();

										int percentRatio = 100 * whitePixelCounter / pixelCounter;

										// Exclude empty boxes.
										if (percentRatio < 95)
										{
											Cv2.ImWrite(resultDir + Path.DirectorySeparatorChar + outputFilename + ".jpg", subImg);
										}
									}
									else
									{
										Cv2.ImWrite(resultDir + Path.DirectorySeparatorChar + outputFilename + ".jpg", subImg);
									}
									
								}
							}
							catch (Exception)
							{
								// Ignore it. Outside image.
							}

							characterNum++;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Processing: " + Path.GetFileNameWithoutExtension(pathFile) + ", Error: " + ex.Message);
			}
		}

		public class FormExtractionOptions
		{
			public int ResizeWidth { get; set; }
			public int JunctionWidth { get; set; }
			public int JunctionHeight { get; set; }

			public int MinNumElements { get; set; }
			public int MaxJunctions { get; set; }
			public int MaxSolutions { get; set; }

			public bool ShowDebugImage { get; set; }

			public FormExtractionOptions()
			{
				// These values should not change.
				ResizeWidth = 800;
				JunctionWidth = 25;
				JunctionHeight = 15;

				// These values can be changed.

				// Minimum boxes per group.
				MinNumElements = 5;

				// These properties prevent wasting CPU on complex image.
				MaxJunctions = 20000;
				MaxSolutions = 50000;

				ShowDebugImage = false;
			}
		}

		public class FormExtractionResult
		{
			public int ReturnCode { get; set; }
			public int[,] DebugImg { get; set; }
			public List<List<Box>> Boxes { get; set; }
			public TimeSpan Duration { get; set; }
		}

		public static FormExtractionResult ProcessImage(string filename, FormExtractionOptions options = null)
		{
			if (options == null)
			{
				// Assume recommanded parameters.
				options = new FormExtractionOptions();
			}

			var orig = new Mat(filename);
			var image = new Mat(filename, ImreadModes.GrayScale);

			Cv2.AdaptiveThreshold(image, image, 255, AdaptiveThresholdTypes.MeanC, ThresholdTypes.Binary, 9, 4);

			// Resize image if too large.
			if (image.Width > options.ResizeWidth)
			{
				var height = options.ResizeWidth * image.Height / image.Width;
				Cv2.Resize(image, image, new Size(options.ResizeWidth, height));
			}

			Cv2.BitwiseNot(image, image);
			Cv2.Dilate(image, image, Cv2.GetStructuringElement(MorphShapes.Cross, new Size(2, 2)));
			
			MatOfByte mat = new MatOfByte(image);
			MatIndexer<byte> indexer = mat.GetIndexer();

			var row = image.Height;
			var col = image.Width;
			Mat newImage = new Mat(row, col, MatType.CV_8UC3);
			newImage.SetTo(Scalar.Black);

			// We must determine if it "may" be an interesting blob.
			Stopwatch watch = new Stopwatch();
			watch.Start();
			var result = HasBoxes(indexer, row, col, options);
			watch.Stop();
			result.Duration = watch.Elapsed;
			//Console.WriteLine("Duration: " + watch.Elapsed);
			
			// Preview
			if (result.Boxes.Any() && image.Width != 0 && options.ShowDebugImage)
			{
				var img = CreateImage(result.DebugImg, hasColor: true);
				Cv2.BitwiseOr(newImage, img, newImage);

				Cv2.BitwiseNot(image, image);
				int width = 400;
				var height = width * image.Height / image.Width;
				Cv2.Resize(orig, orig, new Size(width, height));
				Cv2.Resize(image, image, new Size(width, height));
				Cv2.Resize(newImage, newImage, new Size(width, height));

				using (new Window("orig", orig))
				using (new Window("pre", image))
				using (new Window("post", newImage))
				{
					Cv2.WaitKey();
					Cv2.DestroyAllWindows();
				}
			}

			// Dispose.
			orig.Dispose();
			image.Dispose();
			newImage.Dispose();
			mat.Dispose();

			return result;
		}

		private class Junction
		{
			public bool Top { get; set; }
			public bool Bottom { get; set; }
			public bool Left { get; set; }
			public bool Right { get; set; }
			public int NumTop { get; set; }
			public int NumBottom { get; set; }
			public int NumLeft { get; set; }
			public int NumRight { get; set; }
			public int X { get; set; }
			public int Y { get; set; }
			public int GroupId { get; set; }
			public double GapX { get; set; }
		}

		private class Line
		{
			public List<Junction> Junctions { get; set; }
			public double GapX { get; set; }
		}

		private class LineCluster
		{
			public int X { get; set; }
			public int Y { get; set; }
			public bool Top { get; set; }
			public bool Bottom { get; set; }
			public List<Junction> Junctions { get; set; }
		}

		private class BoxesCluster
		{
			public LineCluster TopLine { get; set; }
			public LineCluster BottomLine { get; set; }
			public double GapY { get; set; }
		}

		public class Box
		{
			public Point TopLeft { get; set; }
			public Point TopRight { get; set; }
			public Point BottomLeft { get; set; }
			public Point BottomRight { get; set; }
		}

		private static FormExtractionResult HasBoxes(MatIndexer<byte> labels, int row, int col, FormExtractionOptions options)
		{
			// Debug image.
			int[,] debugImg = null;
			if (options.ShowDebugImage)
			{
				debugImg = new int[row, col];
				for (int y = 0; y < row; y++)
					for (int x = 0; x < col; x++)
						debugImg[y, x] = 0;
			}

			// We are seaching for pattern!
			// We look for junctions.
			// This will help us make a decision.
			// Junction types: T, L, +.
			// Junctions allow us to find boxes contours.

			int width = options.JunctionWidth;
			int height = options.JunctionHeight;

			// Cache per line speed up the creation of various cache.
			Dictionary<int, List<Junction>> cacheListJunctionPerLine = new Dictionary<int, List<Junction>>();
			List<Junction> listJunction = new List<Junction>();

			// If there is too much junction near each other, maybe it's just a black spot.
			// We must ignore it to prevent wasting CPU and spend too much time.
			int maxProximity = 10;

			for (int y = 0; y < row; y++)
			{
				List<Junction> listJunctionX = null;
				int proximityCounter = 0;

				for (int x = 0; x < col; x++)
				{
					var junction = GetJunction(labels, row, col, height, width, y, x);
					if (junction != null)
					{
						if (listJunctionX == null)
						{
							listJunctionX = new List<Junction>();
						}
						listJunctionX.Add(junction);
						proximityCounter++;
					}
					else
					{
						if (listJunctionX != null)
						{
							if (proximityCounter < maxProximity)
							{
								if (!cacheListJunctionPerLine.ContainsKey(y))
								{
									cacheListJunctionPerLine.Add(y, new List<Junction>());
								}
								cacheListJunctionPerLine[y].AddRange(listJunctionX);
								listJunction.AddRange(listJunctionX);
								listJunctionX.Clear();
							}
							else
							{
								listJunctionX.Clear();
							}
						}
						proximityCounter = 0;
					}
				}

				if (proximityCounter < maxProximity && listJunctionX != null)
				{
					if (!cacheListJunctionPerLine.ContainsKey(y))
					{
						cacheListJunctionPerLine.Add(y, new List<Junction>());
					}
					cacheListJunctionPerLine[y].AddRange(listJunctionX);
					listJunction.AddRange(listJunctionX);
				}
			}
			
			// Console.WriteLine("Junction.count: " + listJunction.Count);

			if (listJunction.Count >= options.MaxJunctions)
			{
				// Something wrong happen. Too much junction for now.
				// If we continue, we would spend too much time processing the image.
				// Let's suppose we don't know.
				return new FormExtractionResult
				{
					// Too many junctions. The image seem too complex. You may want to increase MaxJunctions
					ReturnCode = 10
				};
			}
			
			// Let's check the list of points.

			// Search near same line.

			// Prepare cache to speedup searching algo.
			Dictionary<Junction, List<Junction>> cacheNearJunction = new Dictionary<Junction, List<Junction>>();
			Dictionary<Junction, List<Junction>> cachePossibleNextJunctionRight = new Dictionary<Junction, List<Junction>>();
			Dictionary<Junction, List<Junction>> cachePossibleNextJunctionLeft = new Dictionary<Junction, List<Junction>>();
			int minX = 10;
			int maxX = 70;
			foreach (var junction in cacheListJunctionPerLine.SelectMany(m => m.Value))
			{
				var listJunctionNearJunction = new List<Junction>();

				for (int deltaY = -3; deltaY <= 3; deltaY++)
				{
					if (cacheListJunctionPerLine.ContainsKey(junction.Y - deltaY))
						listJunctionNearJunction.AddRange(cacheListJunctionPerLine[junction.Y - deltaY]);
				}

				var list = listJunctionNearJunction
					.Where(m =>
						Math.Abs(m.X - junction.X) <= maxX
						)
					.ToList();

				cacheNearJunction.Add(junction, list);

				var possibleNextJunction = list
					.Where(m =>
						Math.Abs(m.X - junction.X) >= minX
						)
					.ToList();

				cachePossibleNextJunctionLeft.Add(junction, possibleNextJunction.Where(m => m.X < junction.X).ToList());
				cachePossibleNextJunctionRight.Add(junction, possibleNextJunction.Where(m => m.X > junction.X).ToList());
			}

			int numSol = 0;

			List<Line> possibleSol = new List<Line>();

			// We use a dictionary here because we need a fast way to remove entry.
			// We reduce computation and we also merge solutions.
			var elements = listJunction.OrderBy(m => m.Y).ToDictionary(m => m, m => m);

			int skipSol = 0;
			while (elements.Any())
			{
				var start = elements.First().Key;
				elements.Remove(start);

				List<Line> listSolutions = new List<Line>();
				var junctionsForGap = cacheNearJunction[start];

				for (int iGap = 0; iGap < junctionsForGap.Count; iGap++)
				{
					var gap = junctionsForGap[iGap];

					// Useless because it's already done with: cacheNearJunction.
					/*
					var gapY = Math.Abs(gap.Y - start.Y);
					if (gapY > 2)
					{
						continue;
					}*/

					var gapX = Math.Abs(gap.X - start.X);
					if (gapX <= 15 || gapX > 50)
					{
						continue;
					}

					// We will reduce list of solution by checking if the solution is already found.
					if (listSolutions.Any(m => Math.Abs(m.GapX - gapX) < 2 && m.Junctions.Contains(start)))
					{
						skipSol++;
						continue;
					}

					List<Junction> curSolution = new List<Junction>();
					curSolution.Add(start);

					int numElementsRight = FindElementsOnDirection(cachePossibleNextJunctionRight, start, gap, gapX, curSolution);
					int numElementsLeft = FindElementsOnDirection(cachePossibleNextJunctionLeft, start, gap, -gapX, curSolution);
					
					int numElements = numElementsLeft + numElementsRight;

					if (numElements >= options.MinNumElements)
					{
						if (numSol == options.MaxSolutions)
						{
							// Something wrong happen. Too much solution for now.
							// If we continue, we would spend too much time processing the image.
							// Let's suppose we don't know.
							return new FormExtractionResult
							{
								// Too much solution. You may want to increase MaxSolutions.
								ReturnCode = 30
							};
						}

						numSol++;
						listSolutions.Add(new Line
						{
							GapX = gapX,
							Junctions = curSolution
						});
					}
				}

				Line bestSol = listSolutions.OrderByDescending(m => m.Junctions.Count).FirstOrDefault();

				if (bestSol != null)
				{
					// Too slow. (faster if we skip removal)
					// But, we have more solutions.
					foreach (var item in bestSol.Junctions)
					{
						elements.Remove(item);
					}

					possibleSol.Add(bestSol);
				}
			}

			//Console.WriteLine("Skip: " + skipSol);
			//Console.WriteLine(numSol + " : Solution found");
			//Console.WriteLine(possibleSol.Count + " Best solution found");

			// Let's merge near junctions. (vertical line)
			// We assign a group id for each clusters.

			int nextGroupId = 1;
			foreach (var curSolution in possibleSol)
			{
				if (curSolution.Junctions.First().GroupId == 0)
				{
					curSolution.Junctions.ForEach(m => m.GapX = curSolution.GapX);

					// Not assigned yet.

					// Find near junction.
					int groupId = 0;

					foreach (var item in curSolution.Junctions)
					{
						var alreadyClassified = cacheNearJunction[item]
							.Where(m =>
								m.GroupId != 0 &&
								Math.Abs(m.X - item.X) <= 5 &&
								Math.Abs(m.Y - item.Y) <= 3 &&
								Math.Abs(m.GapX - item.GapX) <= 2
							);
						if (alreadyClassified.Any())
						{
							groupId = alreadyClassified.First().GroupId;
							break;
						}
					}

					if (groupId == 0)
					{
						// Not found.

						// Create a new group.
						nextGroupId++;

						groupId = nextGroupId;
					}

					curSolution.Junctions.ForEach(m => m.GroupId = groupId);
				}
			}

			Dictionary<int, List<Junction>> junctionsPerGroup = possibleSol
				.SelectMany(m => m.Junctions)
				.GroupBy(m => m.GroupId)
				.ToDictionary(m => m.Key, m => m.ToList());

			// Let's explore the clusters directions and try to interconnect clusters on the horizontal side.

			// Minimum percent of elements to determine the direction.
			int minElementPercent = 60;

			List<LineCluster> lineClusters = new List<LineCluster>();

			foreach (var item in junctionsPerGroup)
			{
				int groupId = item.Key;
				List<Junction> junctions = item.Value;

				int minElementDir = minElementPercent * junctions.Count / 100;

				// Determine the general direction.
				var top = junctions.Count(m => m.Top) > minElementDir;
				var bottom = junctions.Count(m => m.Bottom) > minElementDir;

				junctions.ForEach(m => m.Top = top);
				junctions.ForEach(m => m.Bottom = bottom);

				var x = (int)junctions.Average(m => m.X);
				var y = (int)junctions.Average(m => m.Y);

				lineClusters.Add(new LineCluster
				{
					Bottom = bottom,
					Top = top,
					Junctions = junctions,
					X = x,
					Y = y
				});
			}

			List<BoxesCluster> boxesClusters = new List<BoxesCluster>();
			Dictionary<LineCluster, LineCluster> lineClustersTop = new Dictionary<LineCluster, LineCluster>();
			Dictionary<LineCluster, LineCluster> lineClustersBottom = new Dictionary<LineCluster, LineCluster>();

			Dictionary<LineCluster, double> cacheGapX = new Dictionary<LineCluster, double>();

			// Merge top and bottom lines.
			foreach (var itemA in lineClusters)
			{
				foreach (var itemB in lineClusters)
				{
					if (itemA != itemB)
					{
						if (itemA.Bottom && itemB.Top || itemA.Top && itemB.Bottom)
						{
							// Compatible.
							var topLine = itemA.Top ? itemA : itemB;
							var bottomLine = itemA.Top ? itemB : itemA;

							if (lineClustersTop.ContainsKey(topLine))
								continue;
							if (lineClustersBottom.ContainsKey(bottomLine))
								continue;


							if (!cacheGapX.ContainsKey(itemA))
								cacheGapX.Add(itemA, itemA.Junctions.Average(m => m.GapX));
							if (!cacheGapX.ContainsKey(itemB))
								cacheGapX.Add(itemB, itemB.Junctions.Average(m => m.GapX));

							var firstGapX = cacheGapX[itemA];
							var secondGapX = cacheGapX[itemB];

							// GapX should be similar. Otherwise, just ignore it.
							if (Math.Abs(firstGapX - secondGapX) <= 2 && Math.Abs(itemA.X - itemB.X) < 200 && Math.Abs(itemA.Y - itemB.Y) < 200)
							{
								var avgGapX = (firstGapX + secondGapX) / 2;

								var minGapY = Math.Max(10, avgGapX - 5);
								var maxGapY = avgGapX + 5;

								// For the majority of element on top line, we should be able to interconnect
								// with the other line.

								// Must have some common element next to each other.
								int commonElementCounter = 0;

								int minPercent = 90;
								int minCount = Math.Min(topLine.Junctions.Count, bottomLine.Junctions.Count);
								int minimumCommonElements = minCount * minPercent / 100;

								List<double> avgGapY = new List<double>();
								foreach (var topJunction in topLine.Junctions)
								{
									var commonElement = bottomLine.Junctions.Where(m =>
										Math.Abs(topJunction.X - m.X) <= 5 &&
										topJunction.Y - m.Y >= minGapY &&
										topJunction.Y - m.Y <= maxGapY
									);
									if (commonElement.Any())
									{
										avgGapY.Add(commonElement.Average(m => topJunction.Y - m.Y));
										commonElementCounter++;

										if (commonElementCounter >= minimumCommonElements)
										{
											// We can stop now. It's a boxes!
											break;
										}
									}
								}

								if (commonElementCounter >= 1 && commonElementCounter >= minimumCommonElements)
								{
									boxesClusters.Add(new BoxesCluster
									{
										TopLine = topLine,
										BottomLine = bottomLine,
										GapY = avgGapY.Average()
									});

									lineClustersTop.Add(topLine, topLine);
									lineClustersBottom.Add(bottomLine, bottomLine);
								}
							}
						}
					}
				}
			}

			// We can now merge near junctions.
			// We want to find the centroid in order to determine the boxes dimensions and position.
			List<List<Box>> allBoxes = new List<List<Box>>();
			foreach (var boxesCluster in boxesClusters)
			{
				// We will explore points horizontally.
				var allPoints = boxesCluster.TopLine.Junctions.Union(boxesCluster.BottomLine.Junctions).Select(m => m.X)
					.OrderBy(m => m)
					.ToList();

				var avgGapX = boxesCluster.TopLine.Junctions.Average(m => m.GapX);
				var maxDist = Math.Max(10, avgGapX / 2);

				// Sometime, there is missing points. We will interconnect the boxes.
				List<Box> boxes = new List<Box>();
				Box curBoxes = null;

				while (allPoints.Any())
				{
					var listX = new List<int>();

					var x = allPoints[0];
					allPoints.RemoveAt(0);
					listX.Add(x);

					// Remove near points.
					for (int i = 0; i < allPoints.Count; i++)
					{
						var curX = allPoints[i];
						if (Math.Abs(curX - x) < maxDist)
						{
							allPoints.RemoveAt(i);
							i--;
							listX.Add(curX);
						}
					}

					var centroidX = listX.Average();

					var topJunctions = boxesCluster.TopLine.Junctions.Where(m => Math.Abs(m.X - centroidX) < maxDist);
					var bottomJunctions = boxesCluster.BottomLine.Junctions.Where(m => Math.Abs(m.X - centroidX) < maxDist);

					Point? curPointTop = null;
					Point? curPointBottom = null;

					if (bottomJunctions.Any())
					{
						var curX = bottomJunctions.Average(m => m.X);
						var curY = bottomJunctions.Average(m => m.Y);
						curPointTop = new Point(curX, curY);
					}

					if (topJunctions.Any())
					{
						var curX = topJunctions.Average(m => m.X);
						var curY = topJunctions.Average(m => m.Y);
						curPointBottom = new Point(curX, curY);
					}

					if (topJunctions.Any() != bottomJunctions.Any())
					{
						// We should try our best to correct the error.
						// If we use boxesCluster.GapY we can estimate the point.

						if (!curPointTop.HasValue)
							curPointTop = new Point(curPointBottom.Value.X, curPointBottom.Value.Y - boxesCluster.GapY);

						if (!curPointBottom.HasValue)
							curPointBottom = new Point(curPointTop.Value.X, curPointTop.Value.Y + boxesCluster.GapY);
					}

					if (!curPointTop.HasValue && !curPointBottom.HasValue)
					{
						return new FormExtractionResult
						{
							// This should not happen. Please open an issue on GitHub with your image.
							ReturnCode = 20
						};
					}

					if (curBoxes == null)
					{
						curBoxes = new Box();
						curBoxes.TopLeft = curPointTop.Value;
						curBoxes.BottomLeft = curPointBottom.Value;
					}
					else
					{
						curBoxes.TopRight = curPointTop.Value;
						curBoxes.BottomRight = curPointBottom.Value;
						boxes.Add(curBoxes);

						// Prepare the next box. (may not be added)
						curBoxes = new Box();
						curBoxes.TopLeft = curPointTop.Value;
						curBoxes.BottomLeft = curPointBottom.Value;
					}
				}

				if (boxes.Any())
				{
					allBoxes.Add(boxes);
				}
			}

			nextGroupId = 0;

			if (options.ShowDebugImage)
			{
				foreach (var item in lineClusters)
				{
					nextGroupId++;
					foreach (var junction in item.Junctions)
					{
						DrawJunction(debugImg, nextGroupId, junction);
					}
				}

				foreach (var item in boxesClusters)
				{
					nextGroupId++;

					// Debug img:
					foreach (var junction in item.TopLine.Junctions)
					{
						DrawJunction(debugImg, nextGroupId, junction);
					}

					foreach (var junction in item.BottomLine.Junctions)
					{
						DrawJunction(debugImg, nextGroupId, junction);
					}
				}
			}

			// Let's explore boxes!
			// We will check if those boxes seem valid.
			for (int i = 0; i < allBoxes.Count; i++)
			{
				var isValid = true;
				var curBoxes = allBoxes[i];

				if (allBoxes.Count < 2)
				{
					isValid = false;
				}
				else
				{
					var minWidth = curBoxes.Min(m =>
						((m.TopRight.X + m.BottomRight.X) / 2) - ((m.TopLeft.X + m.BottomLeft.X) / 2));
					var minHeight = curBoxes.Min(m =>
						((m.BottomRight.Y + m.BottomLeft.Y) / 2) - ((m.TopRight.Y + m.TopLeft.Y) / 2));
					var maxWidth = curBoxes.Max(m =>
						((m.TopRight.X + m.BottomRight.X) / 2) - ((m.TopLeft.X + m.BottomLeft.X) / 2));
					var maxHeight = curBoxes.Max(m =>
						((m.BottomRight.Y + m.BottomLeft.Y) / 2) - ((m.TopRight.Y + m.TopLeft.Y) / 2));

					// If the width and height are too different, we should not consider the boxes.
					if (maxWidth - minWidth > 7 || maxHeight - minHeight > 5)
					{
						isValid = false;
					}
				}

				if (!isValid)
				{
					allBoxes.RemoveAt(i);
					i--;
				}
			}

			if (options.ShowDebugImage)
			{
				int size = 5;
				foreach (var item in allBoxes)
				{
					nextGroupId++;
					foreach (var box in item)
					{
						DrawPoint(debugImg, nextGroupId, box.TopLeft.X, box.TopLeft.Y, size);
						DrawPoint(debugImg, nextGroupId, box.TopRight.X, box.TopRight.Y, size);
						DrawPoint(debugImg, nextGroupId, box.BottomLeft.X, box.BottomLeft.Y, size);
						DrawPoint(debugImg, nextGroupId, box.BottomRight.X, box.BottomRight.Y, size);

						// Let's show the center of the box.
						var x = (box.TopLeft.X + box.TopRight.X + box.BottomLeft.X + box.BottomRight.X) / 4;
						var y = (box.TopLeft.Y + box.TopRight.Y + box.BottomLeft.Y + box.BottomRight.Y) / 4;
						DrawPoint(debugImg, nextGroupId, x, y, 10);
					}
				}
			}

			FormExtractionResult finalResult = new FormExtractionResult
			{
				Boxes = allBoxes,
				ReturnCode = 0
			};

			if (options.ShowDebugImage)
			{
				finalResult.DebugImg = debugImg;
			}

			return finalResult;
		}

		private static void DrawPoint(int[,] outputImg, int colorCode, int x, int y, int size)
		{
			// Must be centered.
			x -= size / 2;
			y -= size / 2;

			for (int i = 0; i < size; i++)
				for (int j = 0; j < size; j++)
				{
					var curY = y + i;
					var curX = x + j;

					if (curX >= 0 && curY >= 0 && curX < outputImg.GetLength(1) && curY < outputImg.GetLength(0))
						outputImg[y + i, x + j] = colorCode;
				}
		}

		private static void DrawJunction(int[,] outputImg, int colorCode, Junction junction)
		{
			var top = junction.Top;
			var bottom = junction.Bottom;
			var right = junction.Right;
			var left = junction.Left;
			var numTop = junction.NumTop;
			var numBottom = junction.NumBottom;
			var numRight = junction.NumRight;
			var numLeft = junction.NumLeft;
			var x = junction.X;
			var y = junction.Y;

			if (top)
				for (int i = 0; i < numTop; i++)
					outputImg[y - i, x] = colorCode;
			if (bottom)
				for (int i = 0; i < numBottom; i++)
					outputImg[y + i, x] = colorCode;
			if (right)
				for (int i = 0; i < numRight; i++)
					outputImg[y, x + i] = colorCode;
			if (left)
				for (int i = 0; i < numLeft; i++)
					outputImg[y, x - i] = colorCode;
		}

		private static int FindElementsOnDirection(
			Dictionary<Junction, List<Junction>> cacheNearJunction,
			Junction start,
			Junction gap,
			int gapX,
			List<Junction> curSolution)
		{
			int numElements = 0;
			var x = start.X;
			var y = start.Y;
			List<Junction> remainingList = new List<Junction>();
			remainingList.AddRange(cacheNearJunction[gap]);
			while (remainingList.Any())
			{
				// Find which element is next one.
				int indexNextElement = -1;

				for (int iNext = 0; iNext < remainingList.Count; iNext++)
				{
					var cur = remainingList[iNext];
					int curX = cur.X;
					int curY = cur.Y;

					int distX = Math.Abs(x + gapX - curX);

					if (distX <= 1)
					{
						indexNextElement = iNext;
						iNext--;
						numElements++;
						remainingList.Clear();
						remainingList.AddRange(cacheNearJunction[cur]);
						x = curX;
						y = curY;

						curSolution.Add(cur);
						break;
					}
				}
				if (indexNextElement == -1)
				{
					// No element found.
					return numElements;
				}
			}
			return numElements;
		}

		private static Junction GetJunction(MatIndexer<byte> labels, int row, int col, int height, int width, int y, int x)
		{
			var val = GetVal(labels, y, x);
			if (0 < val)
			{
				// Let's explore the directions.

				int numTop = 0;
				if (y - height >= 0)
					for (int i = 0; i < height; i++)
						if (GetVal(labels, y - i, x) == val)
							numTop++;
						else
							break;

				int numBottom = 0;
				if (y + height < row)
					for (int i = 0; i < height; i++)
						if (GetVal(labels, y + i, x) == val)
							numBottom++;
						else
							break;

				int numRight = 0;
				if (x + width < col)
					for (int i = 0; i < width; i++)
						if (GetVal(labels, y, x + i) == val)
							numRight++;
						else
							break;

				int numLeft = 0;
				if (x - width >= 0)
					for (int i = 0; i < width; i++)
						if (GetVal(labels, y, x - i) == val)
							numLeft++;
						else
							break;

				var top = numTop >= height;
				var bottom = numBottom >= height;
				var left = numLeft >= width;
				var right = numRight >= width;

				if ((top || bottom) && (left || right))
				{
					return new Junction
					{
						Bottom = bottom,
						Left = left,
						Right = right,
						Top = top,
						NumBottom = numBottom,
						NumLeft = numLeft,
						NumRight = numRight,
						NumTop = numTop,
						X = x,
						Y = y
					};
				}
			}
			return null;
		}

		private static int GetVal(MatIndexer<byte> labels, int y, int x)
		{
			// OpenCV does not validate index.
			// It does a buffer overflow. (read current pixel and next 2 pixels in the array in grayscale mode)
			// If it's outside the array, it returns 0.
			// The side effect was interesting.
			// It improves horizontal line detection.
			// But the boxes were not centered.
			//return (labels[y, x].Item0 | labels[y, x].Item1 | labels[y, x].Item2);

			// But this time, I will consider the previous pixel and only the next pixel. (on the same line)
			// The boxes are better centered.
			
			return (
				labels[y, x - 1] |
				labels[y, x] |
				labels[y, x + 1]);
		}

		private static Random Random = new Random();
		private static Mat CreateImage(int[,] labels, bool hasColor)
		{
			var row = labels.GetLength(0);
			var col = labels.GetLength(1);

			Dictionary<int, int> cacheColor = new Dictionary<int, int>();
			int color = 0;

			Mat newImage = new Mat(row, col, MatType.CV_8UC3);
			for (int i = 0; i < row; i++)
			{
				for (int j = 0; j < col; j++)
				{
					var val = labels[i, j];

					if (val > 0)
					{
						if (hasColor)
						{
							if (!cacheColor.ContainsKey(val))
							{
								// Generate a random color
								color = (122 + Random.Next(123)) << 16 | (122 + Random.Next(123)) << 8 | (122 + Random.Next(123));
								cacheColor.Add(val, color);
							}
							else
							{
								color = cacheColor[val];
							}
							newImage.Set(i, j, color);
						}
						else
						{
							newImage.Set(i, j, 0xFFFFFF);
						}
					}
					else
					{
						newImage.Set(i, j, 0);
					}
				}
			}
			return newImage;
		}
	}
}