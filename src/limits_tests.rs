use super::*;
use crate::testing::EnvRestore;
use serial_test::serial;

/// Remove every limit env var so an inherited shell value cannot change the
/// default assertions. The process environment is shared, so every test that
/// touches it is `#[serial]` and guarded by `EnvRestore`.
fn clear_env() -> EnvRestore {
    EnvRestore::remove(&[
        INDEX_JOBS_ENV,
        EMBED_THREADS_ENV,
        EMBED_PAUSE_MS_ENV,
        MIN_FREE_MB_ENV,
        MAX_CHUNKS_PER_BATCH_ENV,
    ])
}

#[test]
#[serial]
fn defaults_are_conservative() {
    let _env = clear_env();
    assert_eq!(index_jobs(), DEFAULT_INDEX_JOBS);
    assert_eq!(
        embed_threads(),
        None,
        "unset must leave ONNX at its default (all cores)"
    );
    assert_eq!(embed_pause(), Duration::ZERO);
    assert_eq!(min_free_mb(), 0, "memory floor is opt-in");
    assert_eq!(max_chunks_per_batch(), 0, "chunk cap is opt-in");
}

#[test]
#[serial]
fn env_overrides_parse() {
    let _env = clear_env();
    let _set = EnvRestore::set(&[
        (INDEX_JOBS_ENV, "3"),
        (EMBED_THREADS_ENV, "4"),
        (EMBED_PAUSE_MS_ENV, "25"),
        (MIN_FREE_MB_ENV, "4096"),
        (MAX_CHUNKS_PER_BATCH_ENV, "5000"),
    ]);
    assert_eq!(index_jobs(), 3);
    assert_eq!(embed_threads(), Some(4));
    assert_eq!(embed_pause(), Duration::from_millis(25));
    assert_eq!(min_free_mb(), 4096);
    assert_eq!(max_chunks_per_batch(), 5000);
}

#[test]
#[serial]
fn invalid_values_fall_back() {
    let _env = clear_env();
    let _set = EnvRestore::set(&[
        (INDEX_JOBS_ENV, "0"),
        (EMBED_THREADS_ENV, "0"),
        (EMBED_PAUSE_MS_ENV, "soon"),
        (MIN_FREE_MB_ENV, "lots"),
        (MAX_CHUNKS_PER_BATCH_ENV, "-1"),
    ]);
    assert_eq!(
        index_jobs(),
        DEFAULT_INDEX_JOBS,
        "0 job slots is meaningless"
    );
    assert_eq!(embed_threads(), None, "0 threads means 'ONNX default'");
    assert_eq!(embed_pause(), Duration::ZERO);
    assert_eq!(min_free_mb(), 0);
    assert_eq!(max_chunks_per_batch(), 0);
}

#[tokio::test]
async fn gate_serialises_jobs_when_configured_to_one() {
    let gate = JobGate::with_config(1, 0);
    let first = gate.acquire().await;
    assert!(
        tokio::time::timeout(Duration::from_millis(50), gate.acquire())
            .await
            .is_err(),
        "a second job must wait while the only slot is held"
    );
    drop(first);
    let second = tokio::time::timeout(Duration::from_secs(5), gate.acquire())
        .await
        .expect("dropping the permit must release the slot");
    drop(second);
}

#[tokio::test]
async fn gate_allows_the_configured_number_of_parallel_jobs() {
    let gate = JobGate::with_config(2, 0);
    let first = gate.acquire().await;
    let second = gate.acquire().await;
    assert!(
        tokio::time::timeout(Duration::from_millis(50), gate.acquire())
            .await
            .is_err(),
        "a third job must wait once both slots are held"
    );
    drop(first);
    drop(second);
}

#[tokio::test]
async fn memory_floor_zero_never_waits() {
    let gate = JobGate::with_config_and_wait(1, 0, Duration::from_secs(3600));
    let permit = tokio::time::timeout(Duration::from_millis(200), gate.acquire())
        .await
        .expect("no memory wait when the floor is disabled");
    drop(permit);
}

#[tokio::test]
async fn memory_wait_is_bounded() {
    // A floor no machine can meet: acquire must return after the (shortened)
    // memory wait instead of blocking forever.
    let gate = JobGate::with_config_and_wait(1, u64::MAX, Duration::from_millis(50));
    let start = Instant::now();
    let permit = tokio::time::timeout(Duration::from_secs(5), gate.acquire())
        .await
        .expect("the memory wait must be bounded");
    assert!(start.elapsed() >= Duration::from_millis(50));
    drop(permit);
}

#[tokio::test]
async fn cancelled_gate_waiter_aborts_without_consuming_a_slot() {
    // Regression: a reindex queued behind a long-running job parked on the
    // gate holding its store handles. A cancellation while queued must abort
    // the wait promptly and leave the slot untouched.
    let gate = JobGate::with_config(1, 0);
    let held = gate.acquire().await;
    let token = CancellationToken::new();
    let canceller = token.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(50)).await;
        canceller.cancel();
    });

    let start = Instant::now();
    let outcome = gate.acquire_or_cancel(&token).await;
    assert!(
        outcome.is_none(),
        "a cancelled waiter must not receive a permit"
    );
    assert!(
        start.elapsed() < Duration::from_secs(2),
        "cancellation must abort the wait promptly"
    );

    drop(held);
    // The slot is still free: the cancelled wait never consumed it.
    assert!(gate
        .acquire_or_cancel(&CancellationToken::new())
        .await
        .is_some());
}

#[tokio::test]
async fn pre_cancelled_token_never_takes_a_slot() {
    let gate = JobGate::with_config(1, 0);
    let token = CancellationToken::new();
    token.cancel();
    assert!(gate.acquire_or_cancel(&token).await.is_none());
    assert!(gate
        .acquire_or_cancel(&CancellationToken::new())
        .await
        .is_some());
}

#[tokio::test]
async fn uncancelled_token_proceeds_through_the_gate() {
    let gate = JobGate::with_config(2, 0);
    let token = CancellationToken::new();
    assert!(gate.acquire_or_cancel(&token).await.is_some());
    assert!(gate.acquire_or_cancel(&token).await.is_some());
}

#[tokio::test]
async fn cancelled_token_aborts_the_memory_wait() {
    // Regression: with the memory floor enabled and unmet, a cancellation that
    // lands AFTER the semaphore is acquired must abort the memory wait at once,
    // not after the whole bounded wait. Otherwise a removed/cancelled job keeps
    // its slot and store handles for up to the full wait — the locked/double-open
    // window `acquire_or_cancel` exists to prevent.
    let gate = JobGate::with_config_and_wait(1, u64::MAX, Duration::from_secs(3600));
    let token = CancellationToken::new();
    let canceller = token.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(50)).await;
        canceller.cancel();
    });

    let start = Instant::now();
    let outcome = tokio::time::timeout(Duration::from_secs(5), gate.acquire_or_cancel(&token))
        .await
        .expect("cancellation must wake the memory wait instead of hanging it");
    assert!(
        outcome.is_none(),
        "a waiter cancelled during the memory wait must not receive a permit"
    );
    assert!(
        start.elapsed() < Duration::from_secs(2),
        "the memory wait must abort promptly on cancellation, not run its full bound"
    );
}
