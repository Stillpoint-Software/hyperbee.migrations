using Microsoft.VisualStudio.TestTools.UnitTesting;

// Workers=0 (all CPUs) overwhelms the shared OpenSearch master event queue
// when many test methods PUT _mapping / POST _reindex concurrently, producing
// process_cluster_event_timeout_exception and 503 flakes. Cap at 4 so the
// cluster master keeps up. Adjust upward only after validating the cluster
// keeps event-queue depth reasonable under increased load.
[assembly: Parallelize( Workers = 4, Scope = ExecutionScope.MethodLevel )]
