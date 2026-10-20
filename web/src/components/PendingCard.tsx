import { Alert, Box, Button, Card, CardContent, Chip, LinearProgress, Stack, Typography } from '@mui/material';
import type { PendingArticle } from '../useArticles';

interface Props {
  item: PendingArticle;
  onResend: () => void;
  onDismiss: () => void;
}

/** Shown the moment the user clicks Transform, until the server has stored the article. */
export default function PendingCard({ item, onResend, onDismiss }: Props) {
  return (
    <Card>
      <CardContent sx={{ '&:last-child': { pb: 2 } }}>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'flex-start' }}>
          <Box sx={{ minWidth: 0, flex: 1 }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 600 }} noWrap title={item.url}>
              {item.url}
            </Typography>
            <Typography variant="body2" color="text.secondary">
              just now
            </Typography>
          </Box>
          <Chip size="small" label={item.error ? 'Not sent' : 'Reading article…'} color={item.error ? 'error' : 'primary'} variant="outlined" />
        </Stack>
        {item.error ? (
          <Alert
            severity="error"
            sx={{ mt: 2 }}
            action={
              <Stack direction="row">
                <Button color="inherit" size="small" onClick={onResend}>
                  Retry
                </Button>
                <Button color="inherit" size="small" onClick={onDismiss}>
                  Dismiss
                </Button>
              </Stack>
            }
          >
            {item.error}
          </Alert>
        ) : (
          <LinearProgress sx={{ mt: 2, borderRadius: 1, height: 6 }} />
        )}
      </CardContent>
    </Card>
  );
}
