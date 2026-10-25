//! Background tasks with an orderly shutdown.

use tokio::sync::oneshot;
use tokio::task::JoinHandle;

/// A spawned task that is asked to finish, rather than cancelled mid-step, when its owner is
/// done with it, and that is aborted if it is dropped without being stopped.
pub(crate) struct BackgroundTask {
    handle: JoinHandle<()>,
    shutdown: Option<oneshot::Sender<()>>,
}

impl BackgroundTask {
    /// Spawns `task`, handing it the receiver that resolves when it should finish.
    pub(crate) fn spawn<F, Fut>(task: F) -> Self
    where
        F: FnOnce(oneshot::Receiver<()>) -> Fut,
        Fut: Future<Output = ()> + Send + 'static,
    {
        let (shutdown, receiver) = oneshot::channel();
        Self {
            handle: tokio::spawn(task(receiver)),
            shutdown: Some(shutdown),
        }
    }

    /// Asks the task to finish and waits until it has.
    pub(crate) async fn stop(mut self) {
        if let Some(shutdown) = self.shutdown.take() {
            // The task may already have finished on its own.
            let _ = shutdown.send(());
        }
        let _ = (&mut self.handle).await;
    }
}

impl Drop for BackgroundTask {
    fn drop(&mut self) {
        self.handle.abort();
    }
}
