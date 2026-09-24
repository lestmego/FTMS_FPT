using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Web.WebView2.Wpf;

namespace FTMS.Desktop;

public sealed class WebViewFtmsClient(WebView2 webView, string ftmsUrl) : IFtmsClient
{
    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var url = webView.Source?.AbsoluteUri ?? string.Empty;
        return Task.FromResult(url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("/id/login", StringComparison.OrdinalIgnoreCase) && !url.Contains("/adfs/", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const body = new URLSearchParams({ take: '1000', skip: '0', page: '1', pageSize: '1000',
                search: '', isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                alarmType: '0', isSortByDate: '' });
              const xhr = new XMLHttpRequest();
              xhr.open('POST', '/ihub/request/GetListRequestV12', false);
              xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
              xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
              try { xhr.send(body.toString()); }
              catch (error) { return JSON.stringify({ error: String(error) }); }
              if (xhr.status < 200 || xhr.status >= 300)
                return JSON.stringify({ error: `FTMS API HTTP ${xhr.status}` });

              let response;
              try { response = JSON.parse(xhr.responseText); }
              catch { return JSON.stringify({ error: 'FTMS API trả về dữ liệu không hợp lệ' }); }
              const unwrap = (value, depth = 0) => {
                if (depth > 8 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = unwrap(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = unwrap(child, depth + 1);
                  if (rows.some(x => x && typeof x === 'object')) return rows;
                }
                return [];
              };
              const pick = (row, ...keys) => {
                for (const key of keys) if (row?.[key] !== undefined && row[key] !== null && row[key] !== '') return row[key];
                return null;
              };
              const statusMap = { 'mới': 0, 'tạo mới': 0, 'new': 0, 'phân công': 1, 'assigned': 1,
                'đang thực hiện': 2, 'in progress': 2, 'hoàn thành': 3, 'completed': 3,
                'tạm ngưng': 4, 'paused': 4, 'đóng': 5, 'đã đóng': 5, 'closed': 5,
                'hủy': 7, 'đã hủy': 7, 'cancelled': 7, 'không xử lý': 8 };
              const statusOf = row => {
                const raw = pick(row, 'status','Status','statusId','StatusId','STATUS_ID','STATUSID','statusID');
                if (typeof raw === 'number') return raw;
                if (raw && typeof raw === 'object') return Number(pick(raw, 'id','Id','value','Value') ?? 0);
                const numeric = Number(raw);
                if (raw !== null && raw !== '' && Number.isFinite(numeric)) return numeric;
                const name = String(pick(row, 'statusName','StatusName','STATUS_NAME','statusText','StatusText', 'status') || '')
                  .trim().toLocaleLowerCase('vi-VN');
                return statusMap[name] ?? null;
              };
              const dateOf = value => {
                if (!value) return null;
                if (typeof value === 'string') {
                  const vi = value.match(/(\d{1,2}):(\d{2})(?::(\d{2}))?\s*-?\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (vi) return `${vi[6]}-${vi[5].padStart(2,'0')}-${vi[4].padStart(2,'0')}T${vi[1].padStart(2,'0')}:${vi[2]}:${vi[3] || '00'}+07:00`;
                  const dotNet = value.match(/\/Date\((\d+)(?:[+-]\d+)?\)\//);
                  if (dotNet) return new Date(Number(dotNet[1])).toISOString();
                  if (/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/.test(value))
                    return value.replace(' ', 'T') + '+07:00';
                }
                const parsed = new Date(value);
                return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
              };
              const numberOf = value => {
                if (value === null || value === undefined || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const emailOf = row => {
                const extract = value => String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                const keys = ['mailPoster','MailPoster','posterEmail','PosterEmail','poster','Poster',
                  'senderEmail','SenderEmail','sender','Sender','emailFrom','EmailFrom','fromEmail','FromEmail',
                  'fromAddress','FromAddress','mailFrom','MailFrom'];
                const ignored = ['fti.sd02@fpt.com','ihub.akabot2','ducvm19@fpt.com'];
                if (keys.some(key => ignored.some(value => String(row?.[key] || '').toLowerCase().includes(value)))) return null;
                const sender = keys.map(key => extract(row?.[key])[0]).find(Boolean) || null;
                return sender;
              };
              let rows = unwrap(response);
              const pageSize = 1000;
              for (let page = 2; rows.length >= (page - 1) * pageSize && page <= 50; page++) {
                const nextBody = new URLSearchParams({ take: String(pageSize), skip: String((page - 1) * pageSize),
                  page: String(page), pageSize: String(pageSize), search: '', isMyTicket: '', isAkabot: '',
                  strStatus: '', strRegionID: '', linkDeptId: '', alarmType: '0', isSortByDate: '' });
                const nextRequest = new XMLHttpRequest();
                nextRequest.open('POST', '/ihub/request/GetListRequestV12', false);
                nextRequest.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                nextRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                try { nextRequest.send(nextBody.toString()); } catch { break; }
                if (nextRequest.status < 200 || nextRequest.status >= 300) break;
                let nextResponse;
                try { nextResponse = JSON.parse(nextRequest.responseText); } catch { break; }
                const nextRows = unwrap(nextResponse);
                if (!nextRows.length) break;
                rows.push(...nextRows);
                if (nextRows.length < pageSize) break;
              }
              const tickets = rows.map(row => ({
                code: String(pick(row, 'code','Code','requestCode','RequestCode','REQUEST_CODE','REQUESTCODE','requestNo','RequestNo') || '').trim(),
                status: statusOf(row),
                title: pick(row, 'title','Title','subject','Subject','REQUEST_TITLE'),
                createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt','CREATE_DATE')),
                updatedAt: dateOf(pick(row, 'updateDate','UpdateDate','updatedAt','UpdatedAt','modifyDate','ModifyDate','lastUpdate','LastUpdate')),
                updatedBy: pick(row, 'updatedBy','UpdatedBy','updateBy','UpdateBy','modifiedBy','ModifiedBy','lastUpdateBy','LastUpdateBy','updateStaffName','UpdateStaffName'),
                assigneeId: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId','ASSIGNEE_ID')),
                assigneeName: pick(row, 'agentName','AgentName','staffName','StaffName','assigneeName','AssigneeName'),
                departmentId: numberOf(pick(row, 'deptId','DeptId','departmentId','DepartmentId','DEPARTMENT_ID')),
                departmentName: pick(row, 'department','Department','departmentName','DepartmentName','deptName','DeptName'),
                slaDeviationMinutes: numberOf(pick(row, 'slaDeviation','SlaDeviation','SLA_DEVIATION','slaDeviationMinutes','SlaDeviationMinutes')),
                slaType: numberOf(pick(row, 'typeSLA','TypeSLA','typeSla','slaType','SlaType','SLA_TYPE')),
                latestEmail: (() => {
                  const sender = emailOf(row);
                  const subject = pick(row, 'emailSubject','EmailSubject');
                  const content = pick(row, 'emailContent','EmailContent');
                  const sentAt = dateOf(pick(row, 'emailDate','EmailDate','sendDate','SendDate','sentAt','SentAt'));
                  if (!sender || !sentAt) return null;
                  const body = String(content || '').trim() === String(subject || '').trim() ? null : content;
                  return { id: String(pick(row, 'emailHistoryId','EmailHistoryId','emailId','EmailId','id','Id') || ''),
                    sentAt, from: sender, subject, body };
                })()
              })).filter(x => x.code && x.status !== null);

              // FTMS removes closed tickets from the active list and exposes them through the history API.
              // Read recent closures so a Completed -> Closed transition is still detected in real time.
              const closedTickets = [];
              try {
                const formatDate = date => `${String(date.getMonth() + 1).padStart(2, '0')}/${String(date.getDate()).padStart(2, '0')}/${date.getFullYear()}`;
                const today = new Date();
                const yesterday = new Date(today); yesterday.setDate(today.getDate() - 1);
                const historyParams = new URLSearchParams({
                  searchData: JSON.stringify({ search: '', source: '-1', departmentId: '-1', staffId: '-1',
                    serviceTypeGroup: '-1', serviceType: '-1', requestLevel: '-1', status: '6' }),
                  fromDate: formatDate(yesterday), toDate: formatDate(today), typeSearch: '0',
                  take: '1000', skip: '0', page: '1', pageSize: '1000'
                });
                const historyRequest = new XMLHttpRequest();
                historyRequest.open('GET', '/ihub/list/GetListHistoryRequestByType?' + historyParams.toString(), false);
                historyRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                historyRequest.send();
                if (historyRequest.status >= 200 && historyRequest.status < 300) {
                  const historyRows = unwrap(JSON.parse(historyRequest.responseText));
                  for (const row of historyRows) {
                    const code = String(pick(row, 'code','Code','requestCode','RequestCode') || '').trim();
                    if (!code) continue;
                    closedTickets.push({
                      code, status: 5,
                      title: pick(row, 'title','Title','subject','Subject'),
                      createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt')),
                      updatedAt: dateOf(pick(row, 'closeDate','CloseDate','updatedAt','UpdatedAt')),
                      updatedBy: pick(row, 'userName','UserName','closedBy','ClosedBy','updatedBy','UpdatedBy'),
                      assigneeId: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId')),
                      assigneeName: pick(row, 'userName','UserName','agentName','AgentName','staffName','StaffName'),
                      departmentId: numberOf(pick(row, 'departmentId','DepartmentId','deptId','DeptId')),
                      departmentName: pick(row, 'departmentName','DepartmentName','department','Department'),
                      slaDeviationMinutes: null, slaType: null
                    });
                  }
                }
              } catch {}

              // The endpoint can repeat a request in overlapping pages. Keep the first row because
              // FTMS sorts the current list before older/secondary representations of the same code.
              const byCode = new Map();
              for (const ticket of tickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                if (!byCode.has(key)) byCode.set(key, ticket);
              }
              for (const ticket of closedTickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                if (!byCode.has(key)) byCode.set(key, ticket);
              }
              const allTickets = Array.from(byCode.values());
              return JSON.stringify({ data: allTickets, count: allTickets.length });
            })()
            """;
        var json = await ExecuteJsonStringAsync(script);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            using var nested = JsonDocument.Parse(root.GetString() ?? "null");
            root = nested.RootElement.Clone();
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            var message = error.GetString() ?? "Không thể đọc API FTMS";
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase) || message.Contains("403", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(message);
            throw new InvalidOperationException(message);
        }
        return DeserializeTickets(root.GetRawText());
    }

    private async Task<IReadOnlyList<TicketSnapshot>> GetTicketsLegacyAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visibleCards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              if (visibleCards.length) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                return JSON.stringify(visibleCards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code));
              }
              let result = null;
              try {
                const body = new URLSearchParams({ take: '500', skip: '0', page: '1', pageSize: '500',
                  search: '', isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                  alarmType: '0', isSortByDate: '' });
                const xhr = new XMLHttpRequest(); xhr.open('POST', '/ihub/request/GetListRequestV12', false);
                xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                xhr.send(body.toString());
                if (xhr.status >= 200 && xhr.status < 300) result = JSON.parse(xhr.responseText);
              } catch {}
              const findRows = (value, depth = 0) => {
                if (depth > 6 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return findRows(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = findRows(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = findRows(child, depth + 1);
                  if (rows.length && rows.some(x => x && typeof x === 'object' && ('code' in x || 'Code' in x))) return rows;
                }
                return [];
              };
              let rows = findRows(result);
              const cards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              const hasTicketCode = rows.some(x => x && typeof x === 'object' &&
                (x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE));
              if (cards.length || !hasTicketCode) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                rows = cards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code);
              }
              return JSON.stringify(rows.map(x => ({ code: x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE,
                status: Number(x.status ?? x.Status ?? x.statusId ?? x.StatusId ?? x.STATUS_ID ?? 0),
                title: x.title || x.Title, createdAt: x.createDate || x.CreateDate, assigneeId: x.staffId || x.StaffId,
                assigneeName: x.agentName || x.AgentName || x.staffName || x.StaffName,
                departmentId: x.deptId || x.DeptId, departmentName: x.department || x.Department,
                slaDeviationMinutes: x.slaDeviation ?? x.SlaDeviation, slaType: x.typeSLA ?? x.TypeSLA })));
            })()
            """;
        var json = await ExecuteJsonStringAsync(script);
        return DeserializeTickets(json);
    }

    public async Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var script = $$"""
            (() => {
              try {
                const parseMailDate = value => {
                  if (!value) return null;
                  const normalized = String(value).replace(/(\d{1,2})(?:st|nd|rd|th)\b/gi, '$1').trim();
                  const dotNet = normalized.match(/^\/Date\((\d+)(?:[+-]\d+)?\)\/$/);
                  if (dotNet) return new Date(Number(dotNet[1]));
                  const local = normalized.match(/^(\d{1,2}):(\d{2})(?::(\d{2}))?\s*-?\s*(\d{1,2})\/(\d{1,2})\/(\d{4})$/) ||
                    normalized.match(/^(\d{1,2})\/(\d{1,2})\/(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?$/);
                  if (local) {
                    const timeFirst = /^\d{1,2}:\d{2}/.test(normalized);
                    const [day, month, year, hour, minute, second] = timeFirst
                      ? [local[4], local[5], local[6], local[1], local[2], local[3] || '00']
                      : [local[1], local[2], local[3], local[4], local[5], local[6] || '00'];
                    const iso = `${year}-${month.padStart(2, '0')}-${day.padStart(2, '0')}T${hour.padStart(2, '0')}:${minute}:${second}+07:00`;
                    const parsed = new Date(iso);
                    return Number.isNaN(parsed.getTime()) ? null : parsed;
                  }
                  const isoWithoutZone = normalized.match(/^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/);
                  const parsed = new Date(isoWithoutZone ? normalized.replace(' ', 'T') + '+07:00' : normalized);
                  return Number.isNaN(parsed.getTime()) ? null : parsed;
                };
                const emailAddress = value => String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/i)?.[0] || null;
                const ignoredSender = (...values) => ['fti.sd02@fpt.com','ihub.akabot2','ducvm19@fpt.com']
                  .some(ignored => values.some(value => String(value || '').toLowerCase().includes(ignored)));
                const automatedReceipt = body => {
                  const latest = String(body || '').split(/<hr\b|-----Original Message-----|From:\s*.{0,200}?\b(?:Sent|Date):/i)[0];
                  const text = latest.replace(/<[^>]+>/g, ' ').normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '').toLowerCase();
                  return (text.includes('thong tin yeu cau ho tro') && text.includes('da duoc tiep nhan')) ||
                    (text.includes('thong tin ho tro') && text.includes('ky thuat se kiem tra va phan hoi')) ||
                    text.includes('itsm - phong ho tro khach hang') || text.includes('tong dai 1900 6973');
                };
                const unwrapRows = (value, depth = 0) => {
                  if (depth > 6 || value == null) return [];
                  if (Array.isArray(value)) return value;
                  if (typeof value === 'string') {
                    try { return unwrapRows(JSON.parse(value), depth + 1); } catch { return []; }
                  }
                  if (typeof value !== 'object') return [];
                  for (const key of ['Data','data','Rows','rows','Items','items','Result','result']) {
                    const rows = unwrapRows(value[key], depth + 1);
                    if (rows.length) return rows;
                  }
                  return [];
                };
                const readMailFile = fileId => {
                  if (!fileId) return '';
                  const request = new XMLHttpRequest();
                  request.open('GET', '/ihub/email/ReadMailFromFile?fileId=' + encodeURIComponent(fileId), false);
                  request.send();
                  if (request.status < 200 || request.status >= 300) return '';
                  let content = request.responseText || '';
                  try {
                    const parsed = JSON.parse(content);
                    content = typeof parsed === 'string' ? parsed :
                      parsed?.data || parsed?.Data || parsed?.content || parsed?.Content || parsed?.body || parsed?.Body || content;
                  } catch {}
                  return String(content || '');
                };
                const historyRequest = new XMLHttpRequest();
                historyRequest.open('POST', '/ihub/Email/GetEmailByCode', false);
                // FTMS calls this endpoint through jQuery's default form encoding.
                // A JSON body returns no email rows even though the ticket page shows them.
                historyRequest.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                historyRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                historyRequest.send(new URLSearchParams({ code: {{code}} }).toString());
                if (historyRequest.status >= 200 && historyRequest.status < 300) {
                  let history = unwrapRows(JSON.parse(historyRequest.responseText || '[]'));
                  if (history.length) {
                    const dateValue = value => {
                      const fallback = Object.entries(value || {}).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const raw = value?.sendDate || value?.SendDate || value?.sentDate || value?.SentDate ||
                        value?.emailDate || value?.EmailDate ||
                        value?.createDate || value?.CreateDate || value?.createdDate || value?.CreatedDate ||
                        value?.date || value?.Date || fallback;
                      const parsed = parseMailDate(raw); return parsed?.getTime() || 0;
                    };
                    history.sort((a, b) => dateValue(b) - dateValue(a) || Number(b.id || b.Id || 0) - Number(a.id || a.Id || 0));
                    for (const mail of history) {
                      const emailId = mail.id || mail.Id || mail.emailHistoryId || mail.EmailHistoryId;
                      const fileId = mail.fileId || mail.FileId || mail.FILEID || mail.FILE_ID || mail.emailFileId || mail.EmailFileId;
                      let body = mail.contents || mail.Contents || mail.content || mail.Content || mail.body || mail.Body || '';
                      if (fileId) body = readMailFile(fileId) || body;
                      if (String(body).trim() === String(mail.subject || mail.Subject || '').trim()) body = '';
                      let sender = [mail.mailPoster, mail.MailPoster, mail.posterEmail, mail.PosterEmail,
                        mail.poster, mail.Poster, mail.senderEmail, mail.SenderEmail, mail.sender, mail.Sender,
                        mail.emailFrom, mail.EmailFrom, mail.fromEmail, mail.FromEmail,
                        mail.fromAddress, mail.FromAddress].map(emailAddress).find(Boolean);
                      if (!sender) {
                        const candidates = Object.entries(mail).flatMap(([key, value]) => {
                          if (!/(?:from|sender|poster)/i.test(key)) return [];
                          const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                          return emails.map(email => ({ key, email }));
                        });
                        sender = candidates[0]?.email || null;
                      }
                      if (ignoredSender(sender, mail.poster, mail.Poster, mail.mailPoster, mail.MailPoster) ||
                          automatedReceipt(body)) continue;
                      const fallbackDate = Object.entries(mail).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const rawDate = mail.sendDate || mail.SendDate || mail.sentDate || mail.SentDate ||
                        mail.emailDate || mail.EmailDate ||
                        mail.createDate || mail.CreateDate || mail.createdDate || mail.CreatedDate ||
                        mail.date || mail.Date || fallbackDate || null;
                      const parsedDate = parseMailDate(rawDate);
                      return JSON.stringify({ id: String(emailId || ''),
                        sentAt: parsedDate?.toISOString() || null,
                        from: sender, subject: mail.subject || mail.Subject || null, body });
                    }
                    // All email rows were deliberately excluded; the C88 fallback
                    // must not reintroduce an automated sender.
                    return JSON.stringify(null);
                  }
                }
                const form = new FormData(); form.append('id', 'C88'); form.append('body', JSON.stringify({ p_strCode: {{code}} }));
                const metaRequest = new XMLHttpRequest(); metaRequest.open('POST', '/ihub/code/code', false); metaRequest.send(form);
                if (metaRequest.status < 200 || metaRequest.status >= 300) return JSON.stringify(null);
                const meta = JSON.parse(metaRequest.responseText);
                const rows = unwrapRows(meta);
                if (!rows.length) return JSON.stringify(null);
                const historyValue = row => {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const rawDate = normalized.lasttimeresponse || normalized.lastresponsetime || normalized.timeresponse ||
                    normalized.responsetime || normalized.senddate || normalized.sentat || normalized.createdate;
                  const parsed = parseMailDate(rawDate)?.getTime() || 0;
                  const rawId = normalized.maxeh || normalized.emailhistoryid || normalized.id || 0;
                  return { time: Number.isNaN(parsed) ? 0 : parsed, id: Number(rawId) || 0 };
                };
                const sortedRows = [...rows].sort((a, b) => {
                  const left = historyValue(a), right = historyValue(b);
                  return right.time - left.time || right.id - left.id;
                });
                for (const row of sortedRows) {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const field = (...names) => {
                    for (const name of names) {
                      const value = normalized[name.replace(/[^a-z0-9]/gi, '').toLowerCase()];
                      if (value !== undefined && value !== null && value !== '') return value;
                    }
                    return null;
                  };
                  const fileId = field('FILEID', 'FILE_ID', 'EMAIL_FILE_ID'); let body = '';
                  if (fileId) body = readMailFile(fileId);
                  const emailId = field('MAX_EH', 'EMAIL_HISTORY_ID', 'ID');
                  const rawSentAt = field('LAST_TIME_RESPONSE', 'LAST_RESPONSE_TIME', 'TIME_RESPONSE', 'RESPONSE_TIME', 'SEND_DATE', 'SENT_AT', 'CREATE_DATE');
                  const parsedSentAt = parseMailDate(rawSentAt);
                  let sentAt = parsedSentAt?.toISOString() || null;
                  let sender = ['MAIL_POSTER', 'POSTER_EMAIL', 'POSTER', 'SENDER_EMAIL', 'SENDER',
                    'EMAIL_FROM', 'FROM_EMAIL', 'EMAILFROM', 'FROM_ADDRESS', 'FROMADDRESS', 'FROM']
                    .map(name => emailAddress(field(name))).find(Boolean);
                  if (!sender) {
                    const candidates = Object.entries(row).flatMap(([key, value]) => {
                      if (!/(?:from|sender|poster)/i.test(key)) return [];
                      const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                      return emails.map(email => ({ key, email }));
                    });
                    sender = candidates[0]?.email || null;
                  }
                  if (ignoredSender(sender, field('MAIL_POSTER','POSTER','SENDER','FROM')) || automatedReceipt(body)) continue;
                  return JSON.stringify({ id: String(emailId || fileId || ''), sentAt,
                    from: sender, subject: field('SUBJECT', 'EMAIL_SUBJECT', 'TITLE') || null, body });
                }
                return JSON.stringify(null);
              } catch { return JSON.stringify(null); }
            })()
            """;
        var json = await ExecuteJsonStringAsync(script);
        return DeserializeLatestEmail(json);
    }

    public async Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var route = ticketCode.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || ticketCode.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
            ? "case" : "request";
        var expectedStatus = (int)status;
        var script = $$"""
            (() => {
              try {
                // The ticket page loads its timeline asynchronously from this API.
                // The HTML returned by /edit/{code} does not contain the rendered entries.
                const timelineRequest = new XMLHttpRequest();
                timelineRequest.open('GET', '/ihub/{{route}}/GetTimeLineByCode?code=' + encodeURIComponent({{code}}), false);
                timelineRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                timelineRequest.send();
                if (timelineRequest.status >= 200 && timelineRequest.status < 300) {
                  const unwrap = (value, depth = 0) => {
                    if (depth > 6 || value == null) return [];
                    if (Array.isArray(value)) return value;
                    if (typeof value === 'string') {
                      try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                    }
                    if (typeof value !== 'object') return [];
                    for (const key of ['data','Data','result','Result','rows','Rows']) {
                      const rows = unwrap(value[key], depth + 1);
                      if (rows.length) return rows;
                    }
                    return [];
                  };
                  const parseDate = value => {
                    if (!value) return null;
                    const raw = String(value).trim();
                    const dotNet = raw.match(/^\/Date\((\d+)(?:[+-]\d+)?\)\/$/);
                    if (dotNet) return new Date(Number(dotNet[1])).toISOString();
                    const local = /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/.test(raw);
                    const parsed = new Date(local ? raw.replace(' ', 'T') + '+07:00' : raw);
                    return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
                  };
                  const rows = unwrap(JSON.parse(timelineRequest.responseText || '[]'))
                    .map(row => ({ status: Number(row.status ?? row.Status ?? row.statusId ?? row.StatusId),
                      occurredAt: parseDate(row.createDate ?? row.CreateDate ?? row.date ?? row.Date),
                      // The FTMS timeline labels "Thực hiện" with the assigned staff.
                      // "creator" can be the mail service when an email reopens a ticket.
                      actor: row.staffName ?? row.StaffName ?? row.agentName ?? row.AgentName ??
                        row.creator ?? row.Creator ?? null,
                      id: Number(row.id ?? row.Id ?? 0) }))
                    .filter(row => row.occurredAt && Number.isFinite(row.status))
                    .sort((a, b) => new Date(a.occurredAt) - new Date(b.occurredAt) || a.id - b.id);
                  const changes = rows.filter((row, index) => row.status === {{expectedStatus}} &&
                    (index === 0 || rows[index - 1].status !== row.status));
                  if (changes.length) {
                    const latest = changes[changes.length - 1];
                    return JSON.stringify({ occurredAt: latest.occurredAt, actor: latest.actor });
                  }
                }
                const xhr = new XMLHttpRequest();
                xhr.open('GET', '/ihub/{{route}}/edit/' + encodeURIComponent({{code}}), false);
                xhr.send();
                if (xhr.status < 200 || xhr.status >= 300) return JSON.stringify(null);
                const doc = new DOMParser().parseFromString(xhr.responseText, 'text/html');
                const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                const statusMap = { 'mới': 0, 'tạo mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'đã hủy': 7,
                  'không xử lý': 8 };
                const entries = Array.from(doc.querySelectorAll('li h6')).map(heading => {
                  const item = heading.parentElement;
                  const paragraphs = Array.from(item?.querySelectorAll('p') || []).map(p => normalize(p.textContent));
                  const dateText = paragraphs.find(x => x.startsWith('Ngày:'))?.replace(/^Ngày:\s*/, '') || '';
                  const actor = paragraphs.find(x => x.startsWith('Thực hiện:'))?.replace(/^Thực hiện:\s*/, '') || null;
                  let occurredAt = null;
                  const match = dateText.match(/(\d{1,2}):(\d{2})\s*-\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (match) occurredAt = `${match[5]}-${match[4].padStart(2,'0')}-${match[3].padStart(2,'0')}T${match[1].padStart(2,'0')}:${match[2]}:00+07:00`;
                  const statusText = normalize(heading.textContent);
                  return { status: statusMap[statusText.toLocaleLowerCase('vi-VN')], occurredAt, actor };
                }).filter(x => x.occurredAt && x.status === {{expectedStatus}});
                entries.sort((a, b) => new Date(b.occurredAt).getTime() - new Date(a.occurredAt).getTime());
                return JSON.stringify(entries[0] || null);
              } catch { return JSON.stringify(null); }
            })()
            """;
        var json = await ExecuteJsonStringAsync(script);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(root.GetString() ?? "null");
                root = nested.RootElement.Clone();
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            var actor = root.TryGetProperty("actor", out var actorValue) ? actorValue.GetString() : null;
            var occurredText = root.TryGetProperty("occurredAt", out var occurredValue) ? occurredValue.GetString() : null;
            DateTimeOffset? occurredAt = DateTimeOffset.TryParse(occurredText, out var parsed) ? parsed : null;
            return new StatusHistoryEntry(status, occurredAt, actor);
        }
        catch (JsonException) { return null; }
    }

    public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken)
    {
        webView.Dispatcher.Invoke(() => webView.Source = new Uri(ftmsUrl));
        return Task.CompletedTask;
    }

    private async Task<string> ExecuteJsonStringAsync(string script)
    {
        var operation = await webView.Dispatcher.InvokeAsync(() => webView.ExecuteScriptAsync(script));
        var raw = await operation;
        try { return JsonSerializer.Deserialize<string>(raw) ?? "null"; }
        catch (JsonException) { return raw; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static LatestEmail? DeserializeLatestEmail(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeLatestEmail(root.GetString() ?? "null");
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? ReadString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString() : null;
            var sentAtText = ReadString("sentAt");
            DateTimeOffset? sentAt = DateTimeOffset.TryParse(sentAtText, out var parsed) ? parsed : null;
            return new LatestEmail(ReadString("id"), sentAt, ReadString("from"), ReadString("subject"), ReadString("body"));
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<TicketSnapshot> DeserializeTickets(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeTickets(root.GetString() ?? "null");
            if (root.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TicketSnapshot>>(root.GetRawText(), JsonOptions) ?? [];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) || root.TryGetProperty("Data", out data))
                    return data.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<List<TicketSnapshot>>(data.GetRawText(), JsonOptions) ?? []
                        : [];
            }
        }
        catch (JsonException) { }
        return [];
    }
}
