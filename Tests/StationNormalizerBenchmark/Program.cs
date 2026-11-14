if (args.Length != 1) throw new ArgumentException("Expected an output receipt path.");
NormalizerBenchmark.Run(args[0]);
