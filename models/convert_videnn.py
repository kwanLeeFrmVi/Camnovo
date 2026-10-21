"""Convert the official ViDeNN checkpoint and compare with its TF graph."""
import argparse
import sys
from pathlib import Path
import numpy as np
import torch
import tensorflow as tf
p=argparse.ArgumentParser();p.add_argument('--source',required=True,help='Official ViDeNN repository checkout');p.add_argument('--output',required=True,help='Destination converted.pth');args=p.parse_args()
source_root=Path(args.source).resolve()
sys.path.insert(0,str(Path(__file__).resolve().parent))
from model_adapters import ViDeNNNet
net=ViDeNNNet();reader=tf.train.load_checkpoint(str(source_root/'ckpt_videnn/ViDeNN-tensorflow-0'))
values=reader.get_variable_to_shape_map()
with torch.no_grad():
    for name,layers,prefix in [('spatial',net.spatial,'block'),('temporal',net.temporal,'temp-block')]:
        for i,layer in enumerate(layers,1):
            scope=f'{prefix}{i}/'+('conv2d' if i in (1,20) else f'conv{i}')
            layer.weight.copy_(torch.from_numpy(reader.get_tensor(scope+'/kernel').transpose(3,2,0,1)))
            if layer.bias is not None:layer.bias.copy_(torch.from_numpy(reader.get_tensor(scope+'/bias')))
            if name=='spatial' and 2<=i<=19:
                bn=net.norms[i-2];scope=f'block{i}/batch_normalization/'
                for attribute,key in [('weight','gamma'),('bias','beta'),('running_mean','moving_mean'),('running_var','moving_variance')]:getattr(bn,attribute).copy_(torch.from_numpy(reader.get_tensor(scope+key)))
torch.save(net.state_dict(),args.output)
print('PASS: converted all official ViDeNN spatial/temporal kernels and batch-normalization statistics.')
# Compare the translated network against the supplied TensorFlow graph.
tf.compat.v1.disable_v2_behavior()
tf.compat.v1.logging.set_verbosity(tf.compat.v1.logging.ERROR)
source=source_root/'model_ViDeNN.py'
code=source.read_text().replace('import tensorflow as tf','import tensorflow.compat.v1 as tf')
scope={};exec(compile(code,str(source),'exec'),scope)
rng=np.random.default_rng(123);frames=rng.uniform(.2,.8,(3,32,40,3)).astype(np.float32)
with tf.compat.v1.Session() as session:
    original=scope['ViDeNN'](session)
    saver=tf.compat.v1.train.Saver();saver.restore(session,str(source_root/'ckpt_videnn/ViDeNN-tensorflow-0'))
    spatial=session.run(original.Y,feed_dict={original.X:frames})
    output=session.run(original.Yframes,feed_dict={original.Xframes:spatial.transpose(1,2,0,3).reshape(1,32,40,9)})
net.eval()
with torch.inference_mode():translated=net(torch.from_numpy(frames[...,::-1].copy()).permute(0,3,1,2)).permute(0,2,3,1).numpy()[...,::-1]
error=float(np.max(np.abs(translated-output)))
print(f'ViDeNN TensorFlow/Torch maximum pixel error: {error:.8f}')
assert error<2e-5,error
