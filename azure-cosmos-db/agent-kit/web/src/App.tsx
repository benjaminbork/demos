import { useEffect, useState } from 'react';
import { Alert, Box, Container, Stack, Typography } from '@mui/material';
import HeadphonesIcon from '@mui/icons-material/Headphones';
import { api, type Voice } from './api';
import { useArticles } from './useArticles';
import SubmitForm from './components/SubmitForm';
import ArticleCard from './components/ArticleCard';
import PendingCard from './components/PendingCard';

export default function App() {
  const { articles, pending, loaded, loadError, submit, dismissPending, retry, changeVoice } = useArticles();
  const [voices, setVoices] = useState<Voice[]>([]);
  const [defaultVoice, setDefaultVoice] = useState('');

  useEffect(() => {
    api
      .getVoices()
      .then((v) => {
        setVoices(v.voices);
        setDefaultVoice(v.default);
      })
      .catch(() => undefined);
  }, []);

  const isEmpty = loaded && articles.length === 0 && pending.length === 0;

  return (
    <Container maxWidth="md" sx={{ py: { xs: 3, sm: 5 } }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <HeadphonesIcon color="primary" fontSize="large" />
        <Typography variant="h4" component="h1" sx={{ fontWeight: 700 }}>
          Audio Blog
        </Typography>
      </Stack>
      <Typography color="text.secondary" sx={{ mb: 3 }}>
        Paste an article link and get a narrated MP3 you can listen to anywhere.
      </Typography>

      <SubmitForm voices={voices} defaultVoice={defaultVoice} onSubmit={submit} />

      {loadError && (
        <Alert severity="warning" sx={{ mt: 3 }}>
          {loadError}
        </Alert>
      )}

      <Stack spacing={2} sx={{ mt: 4 }}>
        {pending.map((p) => (
          <PendingCard
            key={`pending-${p.id}`}
            item={p}
            onResend={() => submit(p.url, p.voice, p.id)}
            onDismiss={() => dismissPending(p.id)}
          />
        ))}
        {articles.map((a) => (
          <ArticleCard key={a.id} article={a} voices={voices} onRetry={retry} onChangeVoice={changeVoice} />
        ))}
      </Stack>

      {isEmpty && (
        <Box sx={{ textAlign: 'center', color: 'text.secondary', mt: 6 }}>
          <Typography>No articles yet. Your audio versions will show up here.</Typography>
        </Box>
      )}
    </Container>
  );
}
