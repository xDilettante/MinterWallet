// Second reference for Minter.Protocol: the official Minter JS libraries (minterjs-wallet 6.2.0 / bip39 3.1.0 for
// mnemonics and the m/44'/60'/0'/0/i path, minter-js-sdk 0.46.2 for checks, redeem proofs and Minter Links).
// Writes tests/Minter.Protocol.Checks/Fixtures/js-expected.json. Test keys and mnemonics only.
import { readFileSync, writeFileSync } from 'node:fs';
import bip39 from 'bip39';
import { walletFromMnemonic } from 'minterjs-wallet';
import { issueCheck, RedeemCheckTxData, prepareLink, decodeLink, TX_TYPE } from 'minter-js-sdk';

const fixtures = new URL('../../tests/Minter.Protocol.Checks/Fixtures/', import.meta.url);
const cases = JSON.parse(readFileSync(new URL('cases.json', fixtures), 'utf8'));

const entropies = ['00000000000000000000000000000000', '7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f', '80808080808080808080808080808080',
  'ffffffffffffffffffffffffffffffff', '9e885d952ad362caeb4efe34a8e91bd2', '0000000000000000000000000000000000000000',
  '808080808080808080808080808080808080808080808080', '0000000000000000000000000000000000000000000000000000000000000000',
  'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff', '68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c'];

const mnemonics = entropies.map(entropy => {
  const mnemonic = bip39.entropyToMnemonic(entropy);
  const accounts = [0, 1, 7].map(index => {
    const wallet = walletFromMnemonic(mnemonic, `m/44'/60'/0'/0/${index}`);
    return { index, privateKey: wallet.getPrivateKeyString().replace(/^0x/, ''), address: wallet.getAddressString() };
  });
  return {
    entropy, mnemonic, valid: bip39.validateMnemonic(mnemonic),
    seed: bip39.mnemonicToSeedSync(mnemonic).toString('hex'),
    seedTrezor: bip39.mnemonicToSeedSync(mnemonic, 'TREZOR').toString('hex'),
    accounts,
  };
});

// Wrong checksum (last word changed) and unknown words must be rejected.
const invalid = [
  'abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon',
  'abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandonx',
  'zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo',
  'abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about',
].map(mnemonic => ({ mnemonic, valid: bip39.validateMnemonic(mnemonic) }));

// Checks whose nonce is UTF-8 text (minter-js-sdk takes the nonce as a string and the value in coins).
const pipToCoins = pip => { const s = BigInt(pip).toString().padStart(19, '0'); const w = s.slice(0, -18), f = s.slice(-18).replace(/0+$/, ''); return f ? `${w}.${f}` : w; };
const checks = cases.checks.filter(c => Buffer.from(Buffer.from(c.nonce, 'hex').toString('utf8'), 'utf8').toString('hex') === c.nonce && BigInt(c.value) < 10n ** 30n)
  .map(c => {
    const check = issueCheck({ privateKey: '0x' + c.key, password: c.password, nonce: Buffer.from(c.nonce, 'hex').toString('utf8'), chainId: c.chainId,
      coin: c.coin, value: pipToCoins(c.value), gasCoin: c.gasCoin, dueBlock: Number(c.dueBlock) });
    const proof = new RedeemCheckTxData({ check }, { password: c.password, address: c.redeemer }).proof;
    return { name: c.name, check, proof: proof.replace(/^0x/, '') };
  });

// Minter Links (https://bip.to/tx/…): requests to pay, with and without a message and the sender's gas settings, and
// a check with its password.
const to = 'Mx7633980c000139dd3bd24a3f54e06474fa941e16';
const links = [
  { name: 'send', to, coin: 0, value: '1.5' },
  { name: 'send-payload', to, coin: 1993, value: '0.000001234', payload: 'заказ 42 ✓' },
  { name: 'send-gas', to, coin: 0, value: '12345.678', nonce: 7, gasPrice: 1, gasCoin: 0 },
  { name: 'send-gas-coin', to, coin: 0, value: '1', gasCoin: 1993 },
].map(l => {
  const url = prepareLink({ type: TX_TYPE.SEND, data: { to: l.to, coin: l.coin, value: l.value }, payload: l.payload,
    nonce: l.nonce, gasPrice: l.gasPrice, gasCoin: l.gasCoin });
  const back = decodeLink(url);
  return { ...l, url, decoded: { to: back.data.to, coin: Number(back.data.coin), value: String(back.data.value), payload: back.payload,
    nonce: back.nonce ?? null, gasPrice: back.gasPrice ?? null, gasCoin: back.gasCoin ?? null } };
});
const checkLinks = checks.slice(0, 1).map(c => {
  const password = cases.checks.find(k => k.name === c.name).password;
  return { name: c.name, check: c.check, password, url: prepareLink({ type: TX_TYPE.REDEEM_CHECK, data: { check: c.check }, password }) };
});

writeFileSync(new URL('js-expected.json', fixtures), JSON.stringify({
  source: 'minterjs-wallet 6.2.0, bip39 3.1.0, minter-js-sdk 0.46.2', mnemonics, invalid, checks, links, checkLinks }, null, 2) + '\n');
console.log(`js oracle: ${mnemonics.length} mnemonics, ${invalid.length} invalid, ${checks.length} checks, ${links.length + checkLinks.length} links`);
