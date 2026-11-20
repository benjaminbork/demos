export type Phase = 'reading' | 'generating' | 'ready' | 'failed';
export type Status = 'queued' | 'processing' | 'ready' | 'failed';
export type Step = 'extract' | 'rewrite' | 'synthesize';

export interface Article {
  id: string;
  url: string;
  title?: string;
  siteName?: string;
  byline?: string;
  status: Status;
  phase: Phase;
  step: Step;
  voice: string;
  createdAt: string;
  updatedAt: string;
  spokenPreview?: string;
  spokenChars?: number;
  hasSpokenText: boolean;
  progress?: { done: number; total: number };
  retry?: { attempt: number; maxAttempts: number; nextAttemptAt?: string; lastError?: string };
  failure?: { step: Step; code: string; message: string; at: string };
  audio?: { voice: string; sizeBytes: number; durationSeconds?: number; url: string; downloadUrl: string };
}

export interface Voice {
  id: string;
  label: string;
}

const base = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '');

/** Absolute URL for API paths (audio links are returned as paths). */
export const apiUrl = (path: string) => `${base}${path}`;

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(apiUrl(path), {
      ...init,
      headers: { 'Content-Type': 'application/json', ...init?.headers },
    });
  } catch {
    throw new ApiError("Couldn't reach the server. Check your connection.", 0);
  }
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const problem = await response.json();
      message = problem.detail ?? problem.title ?? message;
    } catch {
      /* not JSON */
    }
    if (response.status === 429) message = 'Too many requests – please wait a minute.';
    throw new ApiError(message, response.status);
  }
  return response.json() as Promise<T>;
}

export const api = {
  listArticles: () => request<{ items: Article[] }>('/api/articles?pageSize=100').then((r) => r.items),
  getVoices: () => request<{ default: string; voices: Voice[] }>('/api/voices'),
  createArticle: (id: string, url: string, voice: string) =>
    request<Article>('/api/articles', { method: 'POST', body: JSON.stringify({ id, url, voice }) }),
  retry: (id: string) => request<Article>(`/api/articles/${id}/retry`, { method: 'POST' }),
  changeVoice: (id: string, voice: string) =>
    request<Article>(`/api/articles/${id}/voice`, { method: 'POST', body: JSON.stringify({ voice }) }),
  getText: (id: string) => request<{ text: string }>(`/api/articles/${id}/text`).then((r) => r.text),
};
