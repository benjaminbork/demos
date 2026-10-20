import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError, type Article } from './api';

/** A card the user just submitted: shown immediately, before (or without) a server response. */
export interface PendingArticle {
  id: string;
  url: string;
  voice: string;
  createdAt: string;
  error?: string;
}

const ACTIVE_POLL_MS = 2000;
const IDLE_POLL_MS = 30000;

export function useArticles() {
  const [articles, setArticles] = useState<Article[]>([]);
  const [pending, setPending] = useState<PendingArticle[]>([]);
  const [loadError, setLoadError] = useState<string>();
  const [loaded, setLoaded] = useState(false);
  const timer = useRef<number>(undefined);

  const refresh = useCallback(async () => {
    try {
      const items = await api.listArticles();
      setArticles(items);
      setPending((p) => p.filter((x) => x.error || !items.some((a) => a.id === x.id)));
      setLoadError(undefined);
    } catch (e) {
      setLoadError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setLoaded(true);
    }
  }, []);

  const busy = pending.some((p) => !p.error) || articles.some((a) => a.status === 'queued' || a.status === 'processing');

  // Poll quickly while something is in progress, slowly otherwise.
  useEffect(() => {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(refresh, busy ? ACTIVE_POLL_MS : IDLE_POLL_MS);
    return () => window.clearTimeout(timer.current);
  }, [articles, pending, busy, refresh]);

  useEffect(() => {
    void refresh();
    const onFocus = () => void refresh();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [refresh]);

  const upsert = (article: Article) =>
    setArticles((list) => {
      const others = list.filter((a) => a.id !== article.id);
      return [article, ...others].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    });

  /** The id is generated here, so re-sending after a network error can't create a duplicate. */
  const submit = async (url: string, voice: string, id: string = crypto.randomUUID()) => {
    const card: PendingArticle = { id, url, voice, createdAt: new Date().toISOString() };
    setPending((p) => [card, ...p.filter((x) => x.id !== id)]);
    try {
      const article = await api.createArticle(id, url, voice);
      upsert(article);
      setPending((p) => p.filter((x) => x.id !== id));
    } catch (e) {
      const message = e instanceof ApiError ? e.message : String(e);
      setPending((p) => p.map((x) => (x.id === id ? { ...x, error: message } : x)));
    }
  };

  const dismissPending = (id: string) => setPending((p) => p.filter((x) => x.id !== id));

  const retry = async (id: string) => upsert(await api.retry(id));
  const changeVoice = async (id: string, voice: string) => upsert(await api.changeVoice(id, voice));

  return { articles, pending, loaded, loadError, submit, dismissPending, retry, changeVoice };
}
