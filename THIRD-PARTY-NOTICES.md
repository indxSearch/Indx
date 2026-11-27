# Third-party notices

The Indx library contains code derived from the open-source projects below. Their licences
ask that the notice follow the code, so this file and the two licence texts in `ThirdParty/`
must ship with any distribution of the library, in source or in binary form.

## FAISS

`Embeddings/HnswGraph.cs` is a port to C# of the HNSW graph of FAISS: the layout of the link
table, the level assignment, the neighbour selection heuristic, the batch build and the greedy
descent, from `faiss/impl/HNSW.cpp` and `faiss/IndexHNSW.cpp`.

- Source: https://github.com/facebookresearch/faiss
- Copyright (c) Meta Platforms, Inc. and affiliates (the licence file reads "Facebook, Inc. and
  its affiliates").
- Licence: MIT. Full text: [`ThirdParty/faiss-LICENSE.txt`](ThirdParty/faiss-LICENSE.txt).

## hnswlib

The same file takes from hnswlib the base-layer search with its stop rule under a filter and
under deletions, marking a node as deleted, and giving a node a new vector in place
(`markDelete`, `searchBaseLayer`, `searchBaseLayerST`, `updatePoint`,
`repairConnectionsForUpdate` in `hnswlib/hnswalg.h`).

- Source: https://github.com/nmslib/hnswlib
- Copyright the hnswlib authors.
- Licence: Apache License, Version 2.0. Full text:
  [`ThirdParty/hnswlib-LICENSE.txt`](ThirdParty/hnswlib-LICENSE.txt).

## Changes made

As the Apache licence asks that changes be stated: the code was translated from C++ to C#;
FAISS's two comparators were reduced to one (a distance, smaller is nearer); the build runs on
`Parallel.For` in place of OpenMP; hnswlib's per-node locks were left out, since the caller lets
one writer in at a time; and when a node is given a new vector, each of its neighbours has its
links chosen again from a narrower set of candidates than hnswlib's. The port and these changes
are Copyright 2026 Bionic as.
